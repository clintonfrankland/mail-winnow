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
    Task<IReadOnlyList<Guid>> GetDueCleanupIdsAsync(CancellationToken cancellationToken = default);
    Task CleanupExpiredAsync(Guid deliveryId, CancellationToken cancellationToken = default);
    Task<MailboxOperationResult> RetryAsync(ClaimsPrincipal actor, Guid deliveryId, CancellationToken cancellationToken = default);
}

/// <summary>Moves MIME content through process memory. The delivery row is the append and source-deletion idempotency record.</summary>
public sealed class MessageDeliveryService(
    MailWinnowDbContext db,
    ICredentialProtectionService credentials,
    IImapConnectionService imap,
    IOptions<LocalImapOptions> localImap,
    IOwnershipAuthorizer ownership,
    IAuditRecorder? audit = null) : IMessageDeliveryService
{
    private static readonly TimeSpan InFlightRecoveryAge = TimeSpan.FromMinutes(5);

    public async Task QueueApprovedAsync(string ownerUserId, Guid headerId, Guid? approvalRuleId = null, CancellationToken cancellationToken = default)
    {
        var header = await OwnedHeaderAsync(ownerUserId, headerId, cancellationToken);
        if (header.EvaluationOutcome != RuleOutcome.Allow) return;
        if (header.BlockedSourceDeletedUtc is not null) return; // a completed block deletion is intentionally irreversible.
        var delivery = await db.MessageDeliveries.SingleOrDefaultAsync(x => x.SourceMessageHeaderId == headerId, cancellationToken);
        if (delivery is null)
        {
            db.MessageDeliveries.Add(new MessageDelivery { SourceMessageHeaderId = headerId, OwnerUserId = ownerUserId, ApprovalRuleId = approvalRuleId });
            if (audit is not null) await audit.RecordAsync("delivery.queued", ownerUserId, ownerUserId, "header", headerId.ToString("N"), cancellationToken: cancellationToken);
        }
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

    public async Task<IReadOnlyList<Guid>> GetDueCleanupIdsAsync(CancellationToken cancellationToken = default)
    {
        var staleBefore = DateTimeOffset.UtcNow.Subtract(InFlightRecoveryAge);
        return await db.MessageDeliveries.AsNoTracking()
            .Where(x => (x.State == MessageDeliveryState.Delivered && x.ExpiresUtc <= DateTimeOffset.UtcNow) ||
                (x.State == MessageDeliveryState.Expired && x.DeletionStartedUtc < staleBefore))
            .OrderBy(x => x.ExpiresUtc).Select(x => x.Id).ToArrayAsync(cancellationToken);
    }

    public async Task CleanupExpiredAsync(Guid deliveryId, CancellationToken cancellationToken = default)
    {
        var current = await db.MessageDeliveries.AsNoTracking().SingleOrDefaultAsync(x => x.Id == deliveryId, cancellationToken);
        if (current is null || current.State == MessageDeliveryState.Deleted || current.ExpiresUtc is null || current.ExpiresUtc > DateTimeOffset.UtcNow) return;
        var now = DateTimeOffset.UtcNow;
        var staleBefore = now.Subtract(InFlightRecoveryAge);
        if (current.State == MessageDeliveryState.Expired && current.DeletionStartedUtc >= staleBefore) return;
        if (current.State is not (MessageDeliveryState.Delivered or MessageDeliveryState.Expired)) return;
        var claimed = await db.MessageDeliveries.Where(x => x.Id == deliveryId && x.State == current.State &&
            (current.State != MessageDeliveryState.Expired || x.DeletionStartedUtc == current.DeletionStartedUtc))
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.State, MessageDeliveryState.Expired).SetProperty(x => x.DeletionStartedUtc, now), cancellationToken);
        if (claimed != 1) return;

        var delivery = await db.MessageDeliveries.SingleAsync(x => x.Id == deliveryId, cancellationToken);
        if (delivery.DestinationMailboxId is null || string.IsNullOrWhiteSpace(delivery.DestinationFolder) || delivery.DestinationUid is null || delivery.DestinationUidValidity is null)
        { await CleanupFailedAsync(delivery, "The delivered message does not have a complete destination identity.", cancellationToken); return; }
        var destination = await db.DestinationMailboxes.SingleOrDefaultAsync(x => x.Id == delivery.DestinationMailboxId && x.OwnerUserId == delivery.OwnerUserId, cancellationToken);
        if (destination is null)
        { await CleanupFailedAsync(delivery, "The destination mailbox used for this delivery is no longer available.", cancellationToken); return; }
        var local = localImap.Value;
        var connection = new ImapConnectionSettings(local.Host, local.Port, local.UseSsl, destination.Username,
            credentials.Unprotect(destination.ProtectedCredential, CredentialKind.DestinationImapPassword));
        var deleted = await imap.DeleteAndExpungeAsync(connection, delivery.DestinationFolder, [delivery.DestinationUid.Value], delivery.DestinationUidValidity.Value, cancellationToken);
        if (!deleted.Succeeded) { await CleanupFailedAsync(delivery, deleted.Error ?? "The destination message could not be deleted.", cancellationToken); return; }
        delivery.State = MessageDeliveryState.Deleted;
        delivery.DeletedUtc = DateTimeOffset.UtcNow;
        delivery.LastFailureStage = null;
        delivery.SanitizedError = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<MailboxOperationResult> RetryAsync(ClaimsPrincipal actor, Guid deliveryId, CancellationToken cancellationToken = default)
    {
        var delivery = await db.MessageDeliveries.SingleOrDefaultAsync(x => x.Id == deliveryId, cancellationToken);
        if (delivery is null) return new(false, "The delivery was not found.");
        ownership.RequireOwner(actor, delivery.OwnerUserId);
        if (delivery.State is MessageDeliveryState.Delivered or MessageDeliveryState.Deleted or MessageDeliveryState.Expired)
            return new(false, "Completed or expired deliveries cannot be retried.");

        // An active Fetching/Delivering claim belongs to another worker. Never turn it
        // into claimable work from an administrative request; doing so could overlap
        // that worker's IMAP APPEND. The conditional update also handles a row that
        // changed state after the authorization read above.
        var retryRequestedUtc = DateTimeOffset.UtcNow;
        var retryRequestedByUserId = ownership.RequireCurrentUserId(actor);
        var updated = await db.MessageDeliveries
            .Where(x => x.Id == deliveryId && x.State == MessageDeliveryState.Failed)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.State, MessageDeliveryState.RetryPending)
                .SetProperty(x => x.RetryRequestedUtc, retryRequestedUtc)
                .SetProperty(x => x.RetryRequestedByUserId, retryRequestedByUserId)
                .SetProperty(x => x.LastFailureStage, (string?)null)
                .SetProperty(x => x.SanitizedError, (string?)null), cancellationToken);
        return updated == 1
            ? new(true, "Delivery retry was queued.")
            : new(false, "Only failed deliveries can be retried; active work remains claimed by its worker.");
    }

    public async Task DeliverAsync(Guid deliveryId, CancellationToken cancellationToken = default)
    {
        var current = await db.MessageDeliveries.AsNoTracking().SingleOrDefaultAsync(x => x.Id == deliveryId, cancellationToken);
        if (current is null || current.State is MessageDeliveryState.Delivered or MessageDeliveryState.Deleted or MessageDeliveryState.Expired) return;

        // A persisted append receipt prevents a second destination copy. Recovery still
        // completes the source deletion before declaring the move delivered.
        if (current.DestinationUid is not null && current.DestinationUidValidity is not null)
        {
            var receiptDelivery = await db.MessageDeliveries.SingleAsync(x => x.Id == deliveryId, cancellationToken);
            await DeleteSourceAndCompleteAsync(receiptDelivery, cancellationToken);
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
                await db.SaveChangesAsync(cancellationToken);
                await DeleteSourceAndCompleteAsync(delivery, cancellationToken);
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
        delivery.DestinationMailboxId = destination.Id;
        delivery.DestinationFolder = destination.Folder;
        await db.SaveChangesAsync(cancellationToken);
        var appended = await imap.AppendMessageAsync(destinationConnection, destination.Folder, fetched.Value!, cancellationToken, header.ReceivedUtc);
        if (!appended.Succeeded || appended.Value is null) { await FailAsync(delivery, "Delivery", appended.Error ?? "The destination append did not return a durable UID.", cancellationToken); return; }

        // Persist the receipt immediately; a later crash resumes by completing this row rather than appending again.
        delivery.DestinationUid = appended.Value;
        await db.SaveChangesAsync(cancellationToken);
        await DeleteSourceAndCompleteAsync(delivery, cancellationToken);
    }

    private async Task<bool> TryClaimAsync(MessageDelivery current, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        var staleBefore = now.Subtract(InFlightRecoveryAge);
        if ((current.State == MessageDeliveryState.Fetching && current.FetchStartedUtc < staleBefore) ||
            (current.State == MessageDeliveryState.Delivering && current.DeliveryStartedUtc < staleBefore))
        {
            // Bind recovery to the precise in-flight claim observed by this worker.
            // A worker with an old snapshot must not reset a claim another worker has
            // already recovered and refreshed while it was waiting to execute.
            var staleClaim = db.MessageDeliveries.Where(x => x.Id == current.Id && x.State == current.State);
            staleClaim = current.State == MessageDeliveryState.Fetching
                ? staleClaim.Where(x => x.FetchStartedUtc == current.FetchStartedUtc)
                : staleClaim.Where(x => x.DeliveryStartedUtc == current.DeliveryStartedUtc);
            await staleClaim.ExecuteUpdateAsync(setters => setters.SetProperty(x => x.State, MessageDeliveryState.RetryPending), token);
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
        var retentionDays = await RetentionDaysAsync(delivery, token);
        delivery.ExpiresUtc = retentionDays is { } days ? delivery.DeliveredUtc.Value.AddDays(days) : null;
        delivery.State = MessageDeliveryState.Delivered;
        delivery.LastFailureStage = null;
        delivery.SanitizedError = null;
        await db.SaveChangesAsync(token);
        if (audit is not null) await audit.RecordAsync("delivery.completed", null, delivery.OwnerUserId, "delivery", delivery.Id.ToString("N"), cancellationToken: token);
    }

    private async Task<int?> RetentionDaysAsync(MessageDelivery delivery, CancellationToken token)
    {
        if (delivery.ApprovalRuleId is null) return null;
        var rule = await db.MailRules.SingleOrDefaultAsync(x => x.Id == delivery.ApprovalRuleId && x.OwnerUserId == delivery.OwnerUserId, token);
        return rule?.DeliveredMessageRetentionDays is { } days ? Math.Max(0, days) : null;
    }

    private async Task DeleteSourceAndCompleteAsync(MessageDelivery delivery, CancellationToken token)
    {
        if (delivery.SourceDeletedUtc is null)
        {
            var header = await OwnedHeaderAsync(delivery.OwnerUserId, delivery.SourceMessageHeaderId, token);
            var source = await db.SourceMailboxes.SingleAsync(x => x.Id == header.SourceMailboxId, token);
            var sourceConnection = new ImapConnectionSettings(source.Host, source.Port, source.UseSsl, source.Username,
                credentials.Unprotect(source.ProtectedCredential, CredentialKind.SourceImapPassword));
            var deleted = await imap.DeleteAndExpungeAsync(sourceConnection, header.FolderName, [header.Uid], header.UidValidity, token);
            if (!deleted.Succeeded)
            {
                await FailAsync(delivery, "Source deletion", deleted.Error ?? "The source message could not be deleted after destination delivery.", token);
                return;
            }
            delivery.SourceDeletedUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(token);
            if (audit is not null) await audit.RecordAsync("source.deleted", null, delivery.OwnerUserId, "header", header.Id.ToString("N"), cancellationToken: token);
        }
        await CompleteAsync(delivery, token);
    }

    private async Task FailAsync(MessageDelivery delivery, string stage, string message, CancellationToken token)
    {
        delivery.State = MessageDeliveryState.Failed;
        delivery.LastFailureStage = stage;
        delivery.SanitizedError = message.Length <= 512 ? message : "The IMAP operation could not be completed.";
        await db.SaveChangesAsync(token);
        if (audit is not null) await audit.RecordAsync("delivery.failed", null, delivery.OwnerUserId, "delivery", delivery.Id.ToString("N"), cancellationToken: token);
    }

    private async Task CleanupFailedAsync(MessageDelivery delivery, string message, CancellationToken token)
    {
        delivery.State = MessageDeliveryState.Expired;
        delivery.LastFailureStage = "Cleanup";
        delivery.SanitizedError = message.Length <= 512 ? message : "The destination message could not be deleted.";
        await db.SaveChangesAsync(token);
    }

    private async Task<SourceMessageHeader> OwnedHeaderAsync(string ownerUserId, Guid headerId, CancellationToken token) =>
        await db.SourceMessageHeaders.SingleOrDefaultAsync(x => x.Id == headerId && db.SourceMailboxes.Any(m => m.Id == x.SourceMailboxId && m.OwnerUserId == ownerUserId), token)
        ?? throw new InvalidOperationException("The message header was not found for this user.");
}
