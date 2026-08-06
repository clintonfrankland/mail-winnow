using System.Security.Claims;
using System.Text.Json;
using MailWinnow.Infrastructure.Persistence;
using MailWinnow.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MailWinnow.Infrastructure.Mailboxes;

public sealed record SourceMailboxSummary(Guid Id, string DisplayName, string Host, int Port,
    bool UseSsl, string Username, bool Enabled, IReadOnlyList<string> SelectedFolders,
    string? PollingStatus, DateTimeOffset? LastSuccessfulConnectionUtc, string? SanitizedError);
public sealed record DestinationMailboxSummary(string Username, string Folder, bool Enabled);
public sealed record SourceMailboxInput(string DisplayName, string Host, int Port, bool UseSsl,
    string Username, string? Password, bool Enabled, IReadOnlyList<string>? SelectedFolders);
public sealed record DestinationMailboxInput(string Username, string? Password, string Folder, bool Enabled);
public sealed record MailboxOperationResult(bool Succeeded, string Message, IReadOnlyList<string>? Folders = null);

public interface IMailboxConfigurationService
{
    Task<IReadOnlyList<SourceMailboxSummary>> ListSourcesAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task<DestinationMailboxSummary?> GetDestinationAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task<MailboxOperationResult> SaveSourceAsync(ClaimsPrincipal actor, Guid? id, SourceMailboxInput input, CancellationToken cancellationToken = default);
    Task<MailboxOperationResult> SaveSourceFoldersAsync(ClaimsPrincipal actor, Guid id, IReadOnlyList<string>? selectedFolders, CancellationToken cancellationToken = default);
    Task<MailboxOperationResult> SetSourceEnabledAsync(ClaimsPrincipal actor, Guid id, bool enabled, CancellationToken cancellationToken = default);
    Task<MailboxOperationResult> TestSourceAsync(ClaimsPrincipal actor, Guid id, bool discoverFolders, CancellationToken cancellationToken = default);
    Task<MailboxOperationResult> SaveDestinationAsync(ClaimsPrincipal actor, DestinationMailboxInput input, CancellationToken cancellationToken = default);
}

public sealed class MailboxConfigurationService(
    MailWinnowDbContext db,
    IOwnershipAuthorizer ownership,
    ICredentialProtectionService credentials,
    IOptions<LocalImapOptions> localImapOptions,
    IImapConnectionService imap,
    IAuditRecorder? audit = null) : IMailboxConfigurationService
{
    public async Task<IReadOnlyList<SourceMailboxSummary>> ListSourcesAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        var ownerId = ownership.RequireCurrentUserId(actor);
        var sources = await db.SourceMailboxes.AsNoTracking().Where(source => source.OwnerUserId == ownerId)
            .OrderBy(source => source.DisplayName).ToListAsync(cancellationToken);
        return sources.Select(ToSummary).ToArray();
    }

    public async Task<DestinationMailboxSummary?> GetDestinationAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        var ownerId = ownership.RequireCurrentUserId(actor);
        return await db.DestinationMailboxes.AsNoTracking().Where(destination => destination.OwnerUserId == ownerId)
            .Select(destination => new DestinationMailboxSummary(destination.Username, destination.Folder, destination.Enabled))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<MailboxOperationResult> SaveSourceAsync(ClaimsPrincipal actor, Guid? id, SourceMailboxInput input, CancellationToken cancellationToken = default)
    {
        var ownerId = ownership.RequireCurrentUserId(actor);
        if (!IsValid(input.DisplayName, input.Host, input.Port, input.Username)) return Invalid();
        var folders = NormalizeFolders(input.SelectedFolders);
        SourceMailbox? source = null;
        var created = id is null;
        if (id is { } sourceId)
        {
            source = await db.SourceMailboxes.SingleOrDefaultAsync(x => x.Id == sourceId, cancellationToken);
            if (source is null) return new(false, "Source mailbox was not found.");
            ownership.RequireOwner(actor, source.OwnerUserId);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(input.Password)) return new(false, "A password is required for a new source mailbox.");
            source = new SourceMailbox { OwnerUserId = ownerId, DisplayName = "", Host = "", Username = "", ProtectedCredential = "" };
            db.SourceMailboxes.Add(source);
        }
        source.DisplayName = input.DisplayName.Trim(); source.Host = input.Host.Trim(); source.Port = input.Port;
        source.UseSsl = input.UseSsl; source.Username = input.Username.Trim(); source.Enabled = input.Enabled;
        source.SelectedFoldersJson = JsonSerializer.Serialize(folders);
        if (!string.IsNullOrWhiteSpace(input.Password)) source.ProtectedCredential = credentials.Protect(input.Password, CredentialKind.SourceImapPassword);
        await db.SaveChangesAsync(cancellationToken);
        if (audit is not null) await audit.RecordAsync(created ? "account.source.created" : "account.source.updated", ownerId, ownerId, "sourceMailbox", source.Id.ToString("N"), cancellationToken: cancellationToken);
        return new(true, "Source mailbox saved.");
    }

    public async Task<MailboxOperationResult> SetSourceEnabledAsync(ClaimsPrincipal actor, Guid id, bool enabled, CancellationToken cancellationToken = default)
    {
        var source = await db.SourceMailboxes.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (source is null) return new(false, "Source mailbox was not found.");
        ownership.RequireOwner(actor, source.OwnerUserId);
        source.Enabled = enabled; await db.SaveChangesAsync(cancellationToken);
        if (audit is not null) await audit.RecordAsync(enabled ? "account.source.enabled" : "account.source.paused", ownership.RequireCurrentUserId(actor), source.OwnerUserId, "sourceMailbox", source.Id.ToString("N"), cancellationToken: cancellationToken);
        return new(true, "Source mailbox updated.");
    }

    public async Task<MailboxOperationResult> SaveSourceFoldersAsync(ClaimsPrincipal actor, Guid id, IReadOnlyList<string>? selectedFolders, CancellationToken cancellationToken = default)
    {
        var source = await db.SourceMailboxes.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (source is null) return new(false, "Source mailbox was not found.");
        ownership.RequireOwner(actor, source.OwnerUserId);
        source.SelectedFoldersJson = JsonSerializer.Serialize(NormalizeFolders(selectedFolders));
        await db.SaveChangesAsync(cancellationToken);
        return new(true, "Selected folders saved.");
    }

    public async Task<MailboxOperationResult> TestSourceAsync(ClaimsPrincipal actor, Guid id, bool discoverFolders, CancellationToken cancellationToken = default)
    {
        var source = await db.SourceMailboxes.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (source is null) return new(false, "Source mailbox was not found.");
        ownership.RequireOwner(actor, source.OwnerUserId);
        try
        {
            var connection = new ImapConnectionSettings(source.Host, source.Port, source.UseSsl, source.Username,
                credentials.Unprotect(source.ProtectedCredential, CredentialKind.SourceImapPassword));
            var folderTest = discoverFolders ? await imap.ListFoldersAsync(connection, cancellationToken) : null;
            var connectionTest = discoverFolders ? null : await imap.TestConnectionAsync(connection, cancellationToken);
            if (!(folderTest?.Succeeded ?? connectionTest!.Succeeded))
            {
                source.PollingStatus = "Failed"; source.SanitizedError = folderTest?.Error ?? connectionTest?.Error;
                await db.SaveChangesAsync(cancellationToken);
                return new(false, source.SanitizedError ?? "Unable to connect or authenticate with this mailbox.");
            }
            var folders = folderTest?.Value;
            source.LastSuccessfulConnectionUtc = DateTimeOffset.UtcNow; source.PollingStatus = "Connected"; source.SanitizedError = null;
            await db.SaveChangesAsync(cancellationToken);
            return new(true, discoverFolders ? "Connection succeeded; folders discovered." : "Connection succeeded.", folders);
        }
        catch (Exception)
        {
            source.PollingStatus = "Failed"; source.SanitizedError = "Unable to connect or authenticate with this mailbox.";
            await db.SaveChangesAsync(cancellationToken);
            return new(false, source.SanitizedError);
        }
    }

    public async Task<MailboxOperationResult> SaveDestinationAsync(ClaimsPrincipal actor, DestinationMailboxInput input, CancellationToken cancellationToken = default)
    {
        var ownerId = ownership.RequireCurrentUserId(actor);
        if (string.IsNullOrWhiteSpace(input.Username) || string.IsNullOrWhiteSpace(input.Folder)) return new(false, "Username and destination folder are required.");
        var destination = await db.DestinationMailboxes.SingleOrDefaultAsync(x => x.OwnerUserId == ownerId, cancellationToken);
        var created = destination is null;
        if (destination is null)
        {
            if (string.IsNullOrWhiteSpace(input.Password)) return new(false, "A password is required for the destination mailbox.");
            destination = new DestinationMailbox { OwnerUserId = ownerId, Username = "", Folder = "", ProtectedCredential = "" }; db.DestinationMailboxes.Add(destination);
        }
        destination.Username = input.Username.Trim(); destination.Folder = input.Folder.Trim(); destination.Enabled = input.Enabled;
        if (!string.IsNullOrWhiteSpace(input.Password)) destination.ProtectedCredential = credentials.Protect(input.Password, CredentialKind.DestinationImapPassword);
        _ = localImapOptions.Value; // destination host, port and TLS are exclusively application configuration.
        await db.SaveChangesAsync(cancellationToken);
        if (audit is not null) await audit.RecordAsync(created ? "credential.destination.created" : "credential.destination.updated", ownerId, ownerId, "destinationMailbox", destination.Id.ToString("N"), cancellationToken: cancellationToken);
        return new(true, "Destination mailbox saved.");
    }

    private static SourceMailboxSummary ToSummary(SourceMailbox source) => new(source.Id, source.DisplayName, source.Host, source.Port, source.UseSsl, source.Username, source.Enabled, DeserializeFolders(source.SelectedFoldersJson), source.PollingStatus, source.LastSuccessfulConnectionUtc, source.SanitizedError);
    private static IReadOnlyList<string> DeserializeFolders(string value) => JsonSerializer.Deserialize<string[]>(value) ?? [];
    private static IReadOnlyList<string> NormalizeFolders(IReadOnlyList<string>? folders) => folders?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.Ordinal).ToArray() ?? [];
    private static bool IsValid(string displayName, string host, int port, string username) => !string.IsNullOrWhiteSpace(displayName) && !string.IsNullOrWhiteSpace(host) && port is > 0 and <= 65535 && !string.IsNullOrWhiteSpace(username);
    private static MailboxOperationResult Invalid() => new(false, "Display name, host, port, and username are required.");
}
