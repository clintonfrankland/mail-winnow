using System.Security.Claims;
using MailWinnow.Core.Rules;
using MailWinnow.Infrastructure.Persistence;
using MailWinnow.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MailWinnow.Infrastructure.Mailboxes;

public interface IMessageDeliveryService
{
    Task QueueApprovedAsync(string ownerUserId, Guid headerId, Guid? approvalRuleId = null, CancellationToken cancellationToken = default);
    Task DeliverAsync(Guid deliveryId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Guid>> GetDueDeliveryIdsAsync(CancellationToken cancellationToken = default);
    Task<MailboxOperationResult> RetryAsync(ClaimsPrincipal actor, Guid deliveryId, CancellationToken cancellationToken = default);
}

/// <summary>Transfers MIME content only in process memory. The delivery row is the idempotency record.</summary>
public sealed class MessageDeliveryService(
    MailWinnowDbContext db,
    ICredentialProtectionService credentials,
    IImapConnectionService imap,
    IOptions<LocalImapOptions> localImap,
    IOwnershipAuthorizer ownership) : IMessageDeliveryService
{
    private static readonly TimeSpan InFlightRecoveryAge = TimeSpan.FromMinutes(5);

    public async Task QueueApprovedAsync(string ownerUserId, Guid headerId, Guid? approvalRuleId = null, CancellationToken cancellationToken = default)
    {
        var header = await OwnedHeaderAsync(ownerUserId, headerId, cancellationToken);
        if (header.EvaluationOutcome != RuleOutcome.Allow) return;
        var delivery = await db.MessageDeliveries.SingleOrDefaultAsync(x => x.SourceMessageHeaderId == headerId, cancellationToken);
        if (delivery is null)
            db.MessageDeliveries.Add(new MessageDelivery { SourceMessageHeaderId = headerId, OwnerUserId = ownerUserId, ApprovalRuleId = approvalRuleId });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> GetDueDeliveryIdsAsync(CancellationToken cancellationToken = default)
    {
        var staleBefore = DateTimeOffset.UtcNow.Subtract(InFlightRecoveryAge);
        return await db.MessageDeliveries.AsNoTracking()
            .Where(x => x.State == MessageDeliveryState.Pending || x.State == MessageDeliveryState.RetryPending ||
                (x.State == MessageDeliveryState.Fetching && x.FetchStartedUtc < staleBefore) ||
                (x.State == MessageDeliveryState.Delivering && x.DeliveryStartedUtc < staleBefore))
            .OrderBy(x => x.CreatedUtc).Select(x => x.Id).ToArrayAsync(cancellationToken);
    }

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

        // A persisted append receipt is finalizable without touching the source or destination again.
        if (delivery.DestinationUid is not null && delivery.DestinationUidValidity is not null)
        {
            await CompleteAsync(delivery, cancellationToken);
            return;
        }

        var staleBefore = DateTimeOffset.UtcNow.Subtract(InFlightRecoveryAge);
        if (delivery.State == MessageDeliveryState.Fetching && delivery.FetchStartedUtc < staleBefore)
            delivery.State = MessageDeliveryState.RetryPending;
        else if (delivery.State == MessageDeliveryState.Delivering && delivery.DeliveryStartedUtc < staleBefore)
            // No receipt means append was never confirmed. Recovery can safely restart the transfer.
            delivery.State = MessageDeliveryState.RetryPending;
        if (delivery.State is not (MessageDeliveryState.Pending or MessageDeliveryState.RetryPending)) return;

        var header = await OwnedHeaderAsync(delivery.OwnerUserId, delivery.SourceMessageHeaderId, cancellationToken);
        var source = await db.SourceMailboxes.SingleAsync(x => x.Id == header.SourceMailboxId, cancellationToken);
        var destination = await db.DestinationMailboxes.SingleOrDefaultAsync(x => x.OwnerUserId == delivery.OwnerUserId && x.Enabled, cancellationToken);
        if (destination is null) { await FailAsync(delivery, "Delivery", "No enabled destination mailbox is configured.", cancellationToken); return; }

        var local = localImap.Value;
        var destinationConnection = new ImapConnectionSettings(local.Host, local.Port, local.UseSsl, destination.Username,
            credentials.Unprotect(destination.ProtectedCredential, CredentialKind.DestinationImapPassword));
        if (delivery.DestinationUidFloor is not null && delivery.DestinationUidValidity is not null)
        {
            var recovered = await imap.GetFolderSnapshotAsync(destinationConnection, destination.Folder, cancellationToken);
            if (!recovered.Succeeded) { await FailAsync(delivery, "Delivery", "The destination append outcome could not be reconciled.", cancellationToken); return; }
            var appendedUid = recovered.Value!.UidValidity == delivery.DestinationUidValidity
                ? recovered.Value.Uids.Where(x => x > delivery.DestinationUidFloor.Value).Order().FirstOrDefault()
                : 0;
            if (appendedUid != 0)
            {
                delivery.DestinationUid = appendedUid;
                await CompleteAsync(delivery, cancellationToken);
                return;
            }
        }

        delivery.State = MessageDeliveryState.Fetching;
        delivery.FetchStartedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        var sourceConnection = new ImapConnectionSettings(source.Host, source.Port, source.UseSsl, source.Username,
            credentials.Unprotect(source.ProtectedCredential, CredentialKind.SourceImapPassword));
        var fetched = await imap.FetchMessageAsync(sourceConnection, header.FolderName, header.Uid, header.UidValidity, cancellationToken);
        if (!fetched.Succeeded) { await FailAsync(delivery, "Fetch", fetched.Error ?? "The original source message could not be fetched.", cancellationToken); return; }

        // Obtain UIDVALIDITY before append. This avoids a post-append metadata lookup turning a successful copy into a retry.
        var destinationSnapshot = await imap.GetFolderSnapshotAsync(destinationConnection, destination.Folder, cancellationToken);
        if (!destinationSnapshot.Succeeded) { await FailAsync(delivery, "Delivery", "The destination folder identity could not be confirmed.", cancellationToken); return; }

        delivery.State = MessageDeliveryState.Delivering;
        delivery.DeliveryStartedUtc = DateTimeOffset.UtcNow;
        delivery.DestinationAppendStartedUtc = delivery.DeliveryStartedUtc;
        delivery.DestinationUidValidity = destinationSnapshot.Value!.UidValidity;
        delivery.DestinationUidFloor = destinationSnapshot.Value.Uids.DefaultIfEmpty().Max();
        await db.SaveChangesAsync(cancellationToken);
        var appended = await imap.AppendMessageAsync(destinationConnection, destination.Folder, fetched.Value!, cancellationToken, header.ReceivedUtc);
        if (!appended.Succeeded || appended.Value is null) { await FailAsync(delivery, "Delivery", appended.Error ?? "The destination append did not return a durable UID.", cancellationToken); return; }

        // Persist the receipt immediately; a later crash resumes by completing this row rather than appending again.
        delivery.DestinationUid = appended.Value;
        await db.SaveChangesAsync(cancellationToken);
        await CompleteAsync(delivery, cancellationToken);
    }

    private async Task CompleteAsync(MessageDelivery delivery, CancellationToken token)
    {
        if (delivery.State == MessageDeliveryState.Delivered) return;
        delivery.DeliveredUtc ??= DateTimeOffset.UtcNow;
        delivery.ExpiresUtc ??= delivery.DeliveredUtc.Value.AddDays(await RetentionDaysAsync(delivery, token));
        delivery.State = MessageDeliveryState.Delivered;
        delivery.LastFailureStage = null;
        delivery.SanitizedError = null;
        await db.SaveChangesAsync(token);
    }

    private async Task<int> RetentionDaysAsync(MessageDelivery delivery, CancellationToken token)
    {
        if (delivery.ApprovalRuleId is null) return Rules.MailRule.DefaultDeliveredMessageRetentionDays;
        var rule = await db.MailRules.SingleOrDefaultAsync(x => x.Id == delivery.ApprovalRuleId && x.OwnerUserId == delivery.OwnerUserId, token);
        return Math.Max(0, rule?.DeliveredMessageRetentionDays ?? Rules.MailRule.DefaultDeliveredMessageRetentionDays);
    }

    private async Task FailAsync(MessageDelivery delivery, string stage, string message, CancellationToken token)
    {
        delivery.State = MessageDeliveryState.Failed;
        delivery.LastFailureStage = stage;
        delivery.SanitizedError = message.Length <= 512 ? message : "The IMAP operation could not be completed.";
        await db.SaveChangesAsync(token);
    }

    private async Task<SourceMessageHeader> OwnedHeaderAsync(string ownerUserId, Guid headerId, CancellationToken token) =>
        await db.SourceMessageHeaders.SingleOrDefaultAsync(x => x.Id == headerId && db.SourceMailboxes.Any(m => m.Id == x.SourceMailboxId && m.OwnerUserId == ownerUserId), token)
        ?? throw new InvalidOperationException("The message header was not found for this user.");
}
