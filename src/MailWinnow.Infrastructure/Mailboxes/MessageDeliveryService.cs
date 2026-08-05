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
        var current = await db.MessageDeliveries.AsNoTracking().SingleOrDefaultAsync(x => x.Id == deliveryId, cancellationToken);
        if (current is null || current.State is MessageDeliveryState.Delivered or MessageDeliveryState.Deleted or MessageDeliveryState.Expired) return;

        // A persisted append receipt is finalizable without touching the source or destination again.
        if (current.DestinationUid is not null && current.DestinationUidValidity is not null)
        {
            var receiptDelivery = await db.MessageDeliveries.SingleAsync(x => x.Id == deliveryId, cancellationToken);
            await CompleteAsync(receiptDelivery, cancellationToken);
            return;
        }

        // The conditional update is an atomic database-backed claim. A concurrent
        // worker may read the row, but only the worker that changes it to Fetching
        // may transfer the MIME message. Stale claims remain recoverable.
        if (!await TryClaimAsync(current, cancellationToken)) return;
        var delivery = await db.MessageDeliveries.SingleAsync(x => x.Id == deliveryId, cancellationToken);

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
            if (recovered.Value!.UidValidity != delivery.DestinationUidValidity)
            {
                await FailAsync(delivery, "Delivery", "The destination folder identity changed before the append outcome could be reconciled.", cancellationToken);
                return;
            }

            // APPEND can succeed even if its response is lost. Never treat an arbitrary
            // post-snapshot UID as this delivery: correlate it to the original Message-ID.
            // If that cannot be proven, do not risk appending a second copy.
            var candidateUids = recovered.Value.Uids.Where(x => x > delivery.DestinationUidFloor.Value).Order().ToArray();
            if (string.IsNullOrWhiteSpace(header.MessageId))
            {
                await FailAsync(delivery, "Delivery", "The ambiguous destination append cannot be reconciled because the source message has no Message-ID.", cancellationToken);
                return;
            }
            var candidates = await imap.FetchHeadersAsync(destinationConnection, destination.Folder, candidateUids, delivery.DestinationUidValidity, cancellationToken);
            if (!candidates.Succeeded)
            {
                await FailAsync(delivery, "Delivery", "The destination append outcome could not be reconciled.", cancellationToken);
                return;
            }
            var matches = candidates.Value!.Where(x => string.Equals(x.MessageId, header.MessageId, StringComparison.Ordinal)).Select(x => x.Uid).Distinct().ToArray();
            if (matches.Length == 1)
            {
                delivery.DestinationUid = matches[0];
                await CompleteAsync(delivery, cancellationToken);
                return;
            }
            await FailAsync(delivery, "Delivery", "The ambiguous destination append could not be safely matched to the original message.", cancellationToken);
            return;
        }

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

    private async Task<bool> TryClaimAsync(MessageDelivery current, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        var staleBefore = now.Subtract(InFlightRecoveryAge);
        if ((current.State == MessageDeliveryState.Fetching && current.FetchStartedUtc < staleBefore) ||
            (current.State == MessageDeliveryState.Delivering && current.DeliveryStartedUtc < staleBefore))
        {
            // The timestamp is evaluated from the snapshot only to decide whether a
            // crashed claim is eligible. The state transition itself is conditional.
            await db.MessageDeliveries.Where(x => x.Id == current.Id && x.State == current.State)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.State, MessageDeliveryState.RetryPending), token);
        }
        var updated = await db.MessageDeliveries
            .Where(x => x.Id == current.Id && (x.State == MessageDeliveryState.Pending || x.State == MessageDeliveryState.RetryPending))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.State, MessageDeliveryState.Fetching)
                .SetProperty(x => x.FetchStartedUtc, now), token);
        return updated == 1;
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
