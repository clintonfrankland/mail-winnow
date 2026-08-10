using MailWinnow.Core.Rules;
using MailWinnow.Infrastructure.Persistence;
using MailWinnow.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MailWinnow.Infrastructure.Mailboxes;

public interface IBlockedMessageDeletionService
{
    Task<IReadOnlyList<Guid>> GetDueHeaderIdsAsync(CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid headerId, CancellationToken cancellationToken = default);
}

/// <summary>Durably moves blocked messages from the source mailbox into the destination Blocked folder.</summary>
public sealed class BlockedMessageDeletionService(
    MailWinnowDbContext db,
    ICredentialProtectionService credentials,
    IImapConnectionService imap,
    IOptions<LocalImapOptions> localImap,
    IAuditRecorder? audit = null) : IBlockedMessageDeletionService
{
    private static readonly TimeSpan StaleClaimAge = TimeSpan.FromMinutes(5);

    public async Task<IReadOnlyList<Guid>> GetDueHeaderIdsAsync(CancellationToken cancellationToken = default)
    {
        var staleBefore = DateTimeOffset.UtcNow.Subtract(StaleClaimAge);
        var candidates = await db.SourceMessageHeaders.AsNoTracking()
            .Where(x => x.EvaluationOutcome == RuleOutcome.Block && x.BlockedSourceDeletedUtc == null)
            .Select(x => new { x.Id, x.ReceivedUtc, x.BlockedSourceDeletionStartedUtc }).ToListAsync(cancellationToken);
        return candidates.Where(x => x.BlockedSourceDeletionStartedUtc is null || x.BlockedSourceDeletionStartedUtc < staleBefore)
            .OrderBy(x => x.ReceivedUtc).Take(1000).Select(x => x.Id).ToArray();
    }

    public async Task DeleteAsync(Guid headerId, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var staleBefore = now.Subtract(StaleClaimAge);
        var snapshot = await db.SourceMessageHeaders.AsNoTracking().SingleOrDefaultAsync(x => x.Id == headerId, cancellationToken);
        if (snapshot is null || snapshot.EvaluationOutcome != RuleOutcome.Block || snapshot.BlockedSourceDeletedUtc is not null ||
            snapshot.BlockedSourceDeletionStartedUtc is { } started && started >= staleBefore) return;
        var claimQuery = db.SourceMessageHeaders.Where(x => x.Id == headerId && x.EvaluationOutcome == RuleOutcome.Block && x.BlockedSourceDeletedUtc == null);
        claimQuery = snapshot.BlockedSourceDeletionStartedUtc is null
            ? claimQuery.Where(x => x.BlockedSourceDeletionStartedUtc == null)
            : claimQuery.Where(x => x.BlockedSourceDeletionStartedUtc == snapshot.BlockedSourceDeletionStartedUtc);
        var claimed = await claimQuery.ExecuteUpdateAsync(setters => setters.SetProperty(x => x.BlockedSourceDeletionStartedUtc, now), cancellationToken);
        if (claimed != 1) return;

        var header = await db.SourceMessageHeaders.SingleAsync(x => x.Id == headerId, cancellationToken);
        await db.Entry(header).ReloadAsync(cancellationToken); // ExecuteUpdate bypasses tracked values.
        var source = await db.SourceMailboxes.SingleAsync(x => x.Id == header.SourceMailboxId, cancellationToken);
        var sourceConnection = new ImapConnectionSettings(source.Host, source.Port, source.UseSsl, source.Username,
            credentials.Unprotect(source.ProtectedCredential, CredentialKind.SourceImapPassword));
        if (header.BlockedDestinationUid is null)
        {
            var destination = await db.DestinationMailboxes.SingleOrDefaultAsync(x => x.OwnerUserId == source.OwnerUserId && x.Enabled, cancellationToken);
            if (destination is null) { await FailAsync(header, "No enabled destination mailbox is configured.", cancellationToken); return; }
            var local = localImap.Value;
            var destinationConnection = new ImapConnectionSettings(local.Host, local.Port, local.UseSsl, destination.Username,
                credentials.Unprotect(destination.ProtectedCredential, CredentialKind.DestinationImapPassword));
            var fetched = await imap.FetchMessageAsync(sourceConnection, header.FolderName, header.Uid, header.UidValidity, cancellationToken);
            if (!fetched.Succeeded) { await FailAsync(header, fetched.Error ?? "The blocked source message could not be fetched.", cancellationToken); return; }
            var appended = await imap.AppendMessageAsync(destinationConnection, "Blocked", fetched.Value!, cancellationToken, header.ReceivedUtc);
            if (!appended.Succeeded || appended.Value is null)
            { await FailAsync(header, appended.Error ?? "The blocked message could not be copied to the destination.", cancellationToken); return; }
            header.BlockedDestinationMailboxId = destination.Id;
            header.BlockedDestinationUid = appended.Value;
            var confirmed = await imap.GetFolderSnapshotAsync(destinationConnection, "Blocked", cancellationToken);
            header.BlockedDestinationUidValidity = confirmed.Succeeded ? confirmed.Value!.UidValidity : null;
            await db.SaveChangesAsync(cancellationToken);
        }

        var deleted = await imap.DeleteAndExpungeAsync(sourceConnection, header.FolderName, [header.Uid], header.UidValidity, cancellationToken);
        if (!deleted.Succeeded)
        {
            await FailAsync(header, deleted.Error ?? "The blocked source message could not be deleted.", cancellationToken);
            return;
        }

        header.BlockedSourceDeletedUtc = DateTimeOffset.UtcNow;
        header.BlockedSourceDeletionStartedUtc = null;
        header.BlockedSourceDeletionError = null;
        await db.SaveChangesAsync(cancellationToken);
        if (audit is not null) await audit.RecordAsync("blocked-source.moved", null, source.OwnerUserId, "header", header.Id.ToString("N"), cancellationToken: cancellationToken);
    }

    private async Task FailAsync(SourceMessageHeader header, string message, CancellationToken token)
    {
        header.BlockedSourceDeletionStartedUtc = null;
        header.BlockedSourceDeletionError = message.Length <= 512 ? message : "The blocked message could not be moved.";
        await db.SaveChangesAsync(token);
    }
}
