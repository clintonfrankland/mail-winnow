using MailWinnow.Infrastructure.Persistence;
using MailWinnow.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MailWinnow.Infrastructure.Mailboxes;

public interface IMailboxRetentionService
{
    Task RunAsync(CancellationToken cancellationToken = default);
}

/// <summary>Applies fixed retention to Mail Winnow's managed destination folders.</summary>
public sealed class MailboxRetentionService(
    MailWinnowDbContext db,
    ICredentialProtectionService credentials,
    IImapConnectionService imap,
    IOptions<LocalImapOptions> localImap,
    ILogger<MailboxRetentionService> logger) : IMailboxRetentionService
{
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var destinations = await db.DestinationMailboxes.AsNoTracking().Where(x => x.Enabled).ToArrayAsync(cancellationToken);
        foreach (var destination in destinations)
        {
            try
            {
                var local = localImap.Value;
                var connection = new ImapConnectionSettings(local.Host, local.Port, local.UseSsl, destination.Username,
                    credentials.Unprotect(destination.ProtectedCredential, CredentialKind.DestinationImapPassword));
                await DeleteAsync(connection, destination.Id, "Blocked", DateTimeOffset.UtcNow.AddDays(-14), cancellationToken);
                await DeleteAsync(connection, destination.Id, "Trash", DateTimeOffset.UtcNow.AddDays(-30), cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Destination retention failed for mailbox {MailboxId}", destination.Id);
            }
        }
    }

    private async Task DeleteAsync(ImapConnectionSettings connection, Guid mailboxId, string folder, DateTimeOffset cutoff, CancellationToken token)
    {
        var result = await imap.DeleteOlderThanAsync(connection, folder, cutoff, createFolderIfMissing: true, token);
        if (!result.Succeeded)
            logger.LogWarning("{Folder} retention failed for destination mailbox {MailboxId}: {Error}", folder, mailboxId, result.Error);
        else if (result.Value > 0)
            logger.LogInformation("Deleted {Count} expired messages from {Folder} for destination mailbox {MailboxId}", result.Value, folder, mailboxId);
    }
}
