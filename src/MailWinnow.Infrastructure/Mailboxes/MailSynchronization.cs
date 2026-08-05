using System.Data;
using System.Security.Claims;
using System.Text.Json;
using MailWinnow.Infrastructure.Persistence;
using MailWinnow.Infrastructure.Rules;
using MailWinnow.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MailWinnow.Infrastructure.Mailboxes;

public sealed class MailSyncOptions
{
    public const string SectionName = "MailSync";
    public int PollingIntervalSeconds { get; set; } = 300;
    public int BatchSize { get; set; } = 100;
    public int MaximumConcurrency { get; set; } = 4;
}

public interface IMailSyncQueue
{
    Task<MailboxOperationResult> RequestAsync(ClaimsPrincipal actor, Guid sourceMailboxId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Guid>> GetDueMailboxIdsAsync(CancellationToken cancellationToken = default);
}

/// <summary>Durable request marker: API requests never communicate directly with IMAP or bypass worker locks.</summary>
public sealed class MailSyncQueue(IServiceScopeFactory scopes) : IMailSyncQueue
{
    public async Task<MailboxOperationResult> RequestAsync(ClaimsPrincipal actor, Guid sourceMailboxId, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
        var ownership = scope.ServiceProvider.GetRequiredService<IOwnershipAuthorizer>();
        var source = await db.SourceMailboxes.SingleOrDefaultAsync(x => x.Id == sourceMailboxId, cancellationToken);
        if (source is null) return new(false, "Source mailbox was not found.");
        ownership.RequireOwner(actor, source.OwnerUserId);
        source.SyncRequestedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return new(true, "Synchronization was queued.");
    }

    public async Task<IReadOnlyList<Guid>> GetDueMailboxIdsAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<MailSyncOptions>>().Value;
        var interval = TimeSpan.FromSeconds(Math.Clamp(options.PollingIntervalSeconds, 15, 86400));
        var cutoff = DateTimeOffset.UtcNow - interval;
        return await db.SourceMailboxes.AsNoTracking().Where(x => x.Enabled &&
                (x.SyncRequestedUtc != null || x.LastSyncAttemptUtc == null || x.LastSyncAttemptUtc < cutoff))
            .Select(x => x.Id).ToArrayAsync(cancellationToken);
    }
}

public interface ISourceMailboxSynchronizer { Task SynchronizeAsync(Guid sourceMailboxId, CancellationToken cancellationToken = default); }

/// <summary>Acquires a per-account synchronization lease. A missing lease means another worker is active.</summary>
public interface ISourceMailboxSyncLockProvider
{
    Task<IAsyncDisposable?> TryAcquireAsync(MailWinnowDbContext db, Guid sourceMailboxId, CancellationToken cancellationToken = default);
}

public sealed class SourceMailboxSynchronizer(
    MailWinnowDbContext db,
    ICredentialProtectionService credentials,
    IImapConnectionService imap,
    IOptions<MailSyncOptions> options,
    ISourceMailboxSyncLockProvider locks,
    IRuleEvaluationService? evaluation = null) : ISourceMailboxSynchronizer
{
    public async Task SynchronizeAsync(Guid sourceMailboxId, CancellationToken cancellationToken = default)
    {
        var source = await db.SourceMailboxes.SingleOrDefaultAsync(x => x.Id == sourceMailboxId, cancellationToken);
        if (source is null || !source.Enabled) return;
        await using var accountLock = await locks.TryAcquireAsync(db, source.Id, cancellationToken);
        if (accountLock is null) return; // a different worker owns this account; its work is sufficient.

        source.LastSyncAttemptUtc = DateTimeOffset.UtcNow;
        source.SyncRequestedUtc = null;
        source.PollingStatus = "Synchronizing";
        await db.SaveChangesAsync(cancellationToken);
        try
        {
            var connection = new ImapConnectionSettings(source.Host, source.Port, source.UseSsl, source.Username,
                credentials.Unprotect(source.ProtectedCredential, CredentialKind.SourceImapPassword));
            var folders = JsonSerializer.Deserialize<string[]>(source.SelectedFoldersJson) ?? [];
            var count = 0;
            foreach (var folder in folders.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal))
                count += await SynchronizeFolderAsync(source, connection, folder, cancellationToken);
            source.LastSyncHeaderCount = count;
            source.LastSyncSucceededUtc = source.LastSuccessfulConnectionUtc = DateTimeOffset.UtcNow;
            source.PollingStatus = "Synchronized";
            source.SanitizedError = null;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            source.PollingStatus = "Failed";
            source.SanitizedError = "Mailbox synchronization could not be completed.";
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    private async Task<int> SynchronizeFolderAsync(SourceMailbox source, ImapConnectionSettings connection, string folder, CancellationToken cancellationToken)
    {
        var snapshot = await imap.GetFolderSnapshotAsync(connection, folder, cancellationToken);
        if (!snapshot.Succeeded) throw new InvalidOperationException(snapshot.Error);
        var state = await db.SourceMailboxFolderSyncStates.SingleOrDefaultAsync(x => x.SourceMailboxId == source.Id && x.FolderName == folder, cancellationToken);
        if (state is null) { state = new() { SourceMailboxId = source.Id, FolderName = folder, UidValidity = snapshot.Value!.UidValidity }; db.SourceMailboxFolderSyncStates.Add(state); }
        else if (state.UidValidity != snapshot.Value!.UidValidity)
        {
            // Keep old namespace entries for audit; the unique key includes UIDVALIDITY, avoiding duplicate or lost headers.
            state.UidValidity = snapshot.Value.UidValidity;
        }
        state.UpdatedUtc = DateTimeOffset.UtcNow;
        var batchSize = Math.Clamp(options.Value.BatchSize, 1, 1000);
        var known = await db.SourceMessageHeaders.Where(x => x.SourceMailboxId == source.Id && x.FolderName == folder && x.UidValidity == state.UidValidity)
            .Select(x => x.Uid).ToListAsync(cancellationToken);
        var pending = snapshot.Value.Uids.Except(known).Order().Take(batchSize).ToArray();
        if (pending.Length == 0) return 0;
        var headers = await imap.FetchHeadersAsync(connection, folder, pending, state.UidValidity, cancellationToken);
        if (!headers.Succeeded) throw new InvalidOperationException(headers.Error);
        var addedHeaderIds = new List<Guid>();
        foreach (var header in headers.Value!)
        {
            var id = Guid.NewGuid();
            db.SourceMessageHeaders.Add(new SourceMessageHeader { Id = id, SourceMailboxId = source.Id, FolderName = folder, UidValidity = state.UidValidity, Uid = header.Uid, MessageId = header.MessageId, Date = header.Date, From = header.From, To = header.To, Subject = header.Subject, ReceivedUtc = DateTimeOffset.UtcNow });
            addedHeaderIds.Add(id);
        }
        await db.SaveChangesAsync(cancellationToken);
        if (evaluation is not null)
            foreach (var headerId in addedHeaderIds) await evaluation.EvaluateAsync(source.OwnerUserId, headerId, DateTimeOffset.UtcNow, cancellationToken);
        return headers.Value!.Count;
    }
}

public sealed class SqlServerAccountLockProvider : ISourceMailboxSyncLockProvider
{
    public Task<IAsyncDisposable?> TryAcquireAsync(MailWinnowDbContext db, Guid sourceMailboxId, CancellationToken cancellationToken = default) =>
        SqlServerAccountLock.TryAcquireAsync(db, sourceMailboxId, cancellationToken);
}

internal sealed class SqlServerAccountLock(MailWinnowDbContext db, string resource) : IAsyncDisposable
{
    public static async Task<IAsyncDisposable?> TryAcquireAsync(MailWinnowDbContext db, Guid id, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "DECLARE @result int; EXEC @result = sp_getapplock @Resource=@resource, @LockMode='Exclusive', @LockOwner='Session', @LockTimeout=0; SELECT @result;";
        var parameter = command.CreateParameter(); parameter.ParameterName = "@resource"; parameter.Value = $"MailWinnow:Sync:{id:N}"; command.Parameters.Add(parameter);
        var result = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        if (result >= 0) return new SqlServerAccountLock(db, (string)parameter.Value);
        await connection.CloseAsync();
        return null;
    }
    public async ValueTask DisposeAsync()
    {
        var connection = db.Database.GetDbConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "EXEC sp_releaseapplock @Resource=@resource, @LockOwner='Session';";
        var parameter = command.CreateParameter(); parameter.ParameterName = "@resource"; parameter.Value = resource; command.Parameters.Add(parameter);
        await command.ExecuteNonQueryAsync();
        await connection.CloseAsync();
    }
}
