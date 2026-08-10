using System.Security.Claims;
using System.Threading.Channels;
using MailWinnow.Infrastructure.Persistence;
using MailWinnow.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MailWinnow.Infrastructure.Mailboxes;

public interface IInboxDeletionQueue
{
    Task<MailboxOperationResult> QueueAsync(ClaimsPrincipal actor, uint uid, uint uidValidity, CancellationToken cancellationToken = default);
}

public sealed class InboxDeletionQueue(IServiceScopeFactory scopes, ILogger<InboxDeletionQueue> logger)
    : BackgroundService, IInboxDeletionQueue
{
    private readonly Channel<InboxDeletion> _queue = Channel.CreateUnbounded<InboxDeletion>(new()
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });

    public async Task<MailboxOperationResult> QueueAsync(ClaimsPrincipal actor, uint uid, uint uidValidity, CancellationToken cancellationToken = default)
    {
        if (uid == 0 || uidValidity == 0) return new(false, "The message identity is invalid.");
        await using var scope = scopes.CreateAsyncScope();
        var ownership = scope.ServiceProvider.GetRequiredService<IOwnershipAuthorizer>();
        var ownerId = ownership.RequireCurrentUserId(actor);
        var db = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
        var destination = await db.DestinationMailboxes.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OwnerUserId == ownerId && x.Enabled, cancellationToken);
        if (destination is null) return new(false, "The destination mailbox is not available.");
        await _queue.Writer.WriteAsync(new(destination.Id, ownerId, destination.Folder, uid, uidValidity), cancellationToken);
        return new(true, "Message deletion was queued.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var deletion in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            var succeeded = false;
            try
            {
                succeeded = await DeleteAsync(deletion, stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Inbox deletion failed for destination mailbox {MailboxId} and UID {Uid}", deletion.DestinationMailboxId, deletion.Uid);
            }
            if (!succeeded && deletion.Attempt < MaximumAttempts)
                _queue.Writer.TryWrite(deletion with { Attempt = deletion.Attempt + 1 });
        }
    }

    private async Task<bool> DeleteAsync(InboxDeletion deletion, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
        var destination = await db.DestinationMailboxes.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == deletion.DestinationMailboxId && x.OwnerUserId == deletion.OwnerUserId && x.Enabled, cancellationToken);
        if (destination is null) return true;
        var credentials = scope.ServiceProvider.GetRequiredService<ICredentialProtectionService>();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<LocalImapOptions>>().Value;
        var connection = new ImapConnectionSettings(options.Host, options.Port, options.UseSsl, destination.Username,
            credentials.Unprotect(destination.ProtectedCredential, CredentialKind.DestinationImapPassword));
        var result = await scope.ServiceProvider.GetRequiredService<IImapConnectionService>()
            .MoveToFolderAsync(connection, deletion.Folder, [deletion.Uid], deletion.UidValidity, "Trash", cancellationToken);
        if (!result.Succeeded)
        {
            logger.LogWarning("Inbox move to Trash could not complete for destination mailbox {MailboxId} and UID {Uid}: {Error}",
                deletion.DestinationMailboxId, deletion.Uid, result.Error);
            return false;
        }
        return true;
    }

    private const int MaximumAttempts = 3;
    private sealed record InboxDeletion(Guid DestinationMailboxId, string OwnerUserId, string Folder, uint Uid, uint UidValidity, int Attempt = 1);
}
