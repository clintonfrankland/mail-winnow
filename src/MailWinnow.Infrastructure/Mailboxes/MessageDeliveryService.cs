using System.Security.Claims;
using MailWinnow.Infrastructure.Persistence;
using MailWinnow.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MailWinnow.Infrastructure.Mailboxes;

public interface IMessageDeliveryService
{
    Task QueueApprovedAsync(string ownerUserId, Guid headerId, CancellationToken cancellationToken = default);
    Task DeliverAsync(Guid deliveryId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Guid>> GetDueDeliveryIdsAsync(CancellationToken cancellationToken = default);
    Task<MailboxOperationResult> RetryAsync(ClaimsPrincipal actor, Guid deliveryId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Transfers a MIME message only in process memory.  The database stores identity, state, and audit metadata,
/// never the fetched message body.  A unique delivery row is the idempotency key for an approved header.
/// </summary>
public sealed class MessageDeliveryService(
    MailWinnowDbContext db,
    ICredentialProtectionService credentials,
    IImapConnectionService imap,
    IOptions<LocalImapOptions> localImap,
    IOwnershipAuthorizer ownership) : IMessageDeliveryService
{
    public async Task QueueApprovedAsync(string ownerUserId, Guid headerId, CancellationToken cancellationToken = default)
    {
        var header = await OwnedHeaderAsync(ownerUserId, headerId, cancellationToken);
        if (header.EvaluationOutcome != Core.Rules.RuleOutcome.Allow) return;
        if (!await db.MessageDeliveries.AnyAsync(x => x.SourceMessageHeaderId == headerId, cancellationToken))
            db.MessageDeliveries.Add(new MessageDelivery { SourceMessageHeaderId = headerId, OwnerUserId = ownerUserId });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> GetDueDeliveryIdsAsync(CancellationToken cancellationToken = default) =>
        await db.MessageDeliveries.AsNoTracking()
            .Where(x => x.State == MessageDeliveryState.Pending || x.State == MessageDeliveryState.RetryPending)
            .OrderBy(x => x.CreatedUtc).Select(x => x.Id).ToArrayAsync(cancellationToken);

    public async Task<MailboxOperationResult> RetryAsync(ClaimsPrincipal actor, Guid deliveryId, CancellationToken cancellationToken = default)
    {
        var delivery = await db.MessageDeliveries.SingleOrDefaultAsync(x => x.Id == deliveryId, cancellationToken);
        if (delivery is null) return new(false, "The delivery was not found.");
        ownership.RequireOwner(actor, delivery.OwnerUserId);
        if (delivery.State is MessageDeliveryState.Delivered or MessageDeliveryState.Deleted or MessageDeliveryState.Expired)
            return new(false, "Completed or expired deliveries cannot be retried.");
        delivery.State = MessageDeliveryState.RetryPending;
        delivery.RetryRequestedUtc = DateTimeOffset.UtcNow;
        delivery.RetryRequestedByUserId = ownership.RequireCurrentUserId(actor);
        delivery.LastFailureStage = null;
        delivery.SanitizedError = null;
        await db.SaveChangesAsync(cancellationToken);
        return new(true, "Delivery retry was queued.");
    }

    public async Task DeliverAsync(Guid deliveryId, CancellationToken cancellationToken = default)
    {
        var delivery = await db.MessageDeliveries.SingleOrDefaultAsync(x => x.Id == deliveryId, cancellationToken);
        if (delivery is null || delivery.State is MessageDeliveryState.Delivered or MessageDeliveryState.Deleted or MessageDeliveryState.Expired) return;
        if (delivery.State is not (MessageDeliveryState.Pending or MessageDeliveryState.RetryPending)) return;

        var header = await OwnedHeaderAsync(delivery.OwnerUserId, delivery.SourceMessageHeaderId, cancellationToken);
        var source = await db.SourceMailboxes.SingleAsync(x => x.Id == header.SourceMailboxId, cancellationToken);
        var destination = await db.DestinationMailboxes.SingleOrDefaultAsync(x => x.OwnerUserId == delivery.OwnerUserId && x.Enabled, cancellationToken);
        if (destination is null) { await FailAsync(delivery, "Delivery", "No enabled destination mailbox is configured.", cancellationToken); return; }

        delivery.State = MessageDeliveryState.Fetching;
        delivery.FetchStartedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        var sourceConnection = new ImapConnectionSettings(source.Host, source.Port, source.UseSsl, source.Username,
            credentials.Unprotect(source.ProtectedCredential, CredentialKind.SourceImapPassword));
        var fetched = await imap.FetchMessageAsync(sourceConnection, header.FolderName, header.Uid, header.UidValidity, cancellationToken);
        if (!fetched.Succeeded) { await FailAsync(delivery, "Fetch", fetched.Error ?? "The original source message could not be fetched.", cancellationToken); return; }

        delivery.State = MessageDeliveryState.Delivering;
        delivery.DeliveryStartedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        var local = localImap.Value;
        var destinationConnection = new ImapConnectionSettings(local.Host, local.Port, local.UseSsl, destination.Username,
            credentials.Unprotect(destination.ProtectedCredential, CredentialKind.DestinationImapPassword));
        var appended = await imap.AppendMessageAsync(destinationConnection, destination.Folder, fetched.Value!, cancellationToken);
        if (!appended.Succeeded) { await FailAsync(delivery, "Delivery", appended.Error ?? "The destination append failed.", cancellationToken); return; }

        var snapshot = await imap.GetFolderSnapshotAsync(destinationConnection, destination.Folder, cancellationToken);
        if (!snapshot.Succeeded) { await FailAsync(delivery, "Delivery", "The destination append succeeded but its UID metadata could not be confirmed.", cancellationToken); return; }
        delivery.DestinationUid = appended.Value;
        delivery.DestinationUidValidity = snapshot.Value!.UidValidity;
        delivery.DeliveredUtc = DateTimeOffset.UtcNow;
        delivery.ExpiresUtc = delivery.DeliveredUtc.Value.AddDays(await RetentionDaysAsync(delivery.OwnerUserId, header, cancellationToken));
        delivery.State = MessageDeliveryState.Delivered;
        delivery.LastFailureStage = null;
        delivery.SanitizedError = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<int> RetentionDaysAsync(string owner, SourceMessageHeader header, CancellationToken token)
    {
        var rule = await db.MailRules.Where(x => x.OwnerUserId == owner && x.Action != Core.Rules.RuleAction.PermanentlyBlock)
            .FirstOrDefaultAsync(token);
        return Math.Max(0, rule?.DeliveredMessageRetentionDays ?? Rules.MailRule.DefaultDeliveredMessageRetentionDays);
    }

    private async Task FailAsync(MessageDelivery delivery, string stage, string message, CancellationToken token)
    {
        delivery.State = MessageDeliveryState.Failed;
        delivery.LastFailureStage = stage;
        // IMAP service errors are already sanitized; do not retain exception details or MIME content.
        delivery.SanitizedError = message.Length <= 512 ? message : "The IMAP operation could not be completed.";
        await db.SaveChangesAsync(token);
    }

    private async Task<SourceMessageHeader> OwnedHeaderAsync(string ownerUserId, Guid headerId, CancellationToken token) =>
        await db.SourceMessageHeaders.SingleOrDefaultAsync(x => x.Id == headerId && db.SourceMailboxes.Any(m => m.Id == x.SourceMailboxId && m.OwnerUserId == ownerUserId), token)
        ?? throw new InvalidOperationException("The message header was not found for this user.");
}
