using System.Security.Claims;
using MailWinnow.Infrastructure.Mailboxes;
using MailWinnow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MailWinnow.Infrastructure.Security;

/// <summary>Sanitized operational audit data. It deliberately has no fields for mail content or credentials.</summary>
public sealed class AuditEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset OccurredUtc { get; set; } = DateTimeOffset.UtcNow;
    public required string EventType { get; set; }
    public string? ActorUserId { get; set; }
    public string? SubjectUserId { get; set; }
    public string? ResourceType { get; set; }
    public string? ResourceId { get; set; }
    public string? Detail { get; set; }
}

public sealed class WorkerHeartbeat
{
    public int Id { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
    public string Status { get; set; } = "Starting";
}

public sealed record AdministrationSnapshot(bool DatabaseHealthy, string LocalImapStatus, DateTimeOffset? WorkerLastSeenUtc,
    int PendingHeaders, int PendingDeliveries, int FailedDeliveries, int ExpirationFailures,
    IReadOnlyList<AdministrationMailboxStatus> Mailboxes, IReadOnlyList<AdministrationDeliveryFailure> DeliveryFailures,
    IReadOnlyList<AuditEvent> AuditEvents);
public sealed record AdministrationMailboxStatus(Guid Id, string OwnerUserId, string DisplayName, bool Enabled, string? Status,
    DateTimeOffset? LastSuccessfulSyncUtc, string? SanitizedError);
public sealed record AdministrationDeliveryFailure(Guid Id, string OwnerUserId, string? Stage, string? SanitizedError);

/// <summary>Records metadata-only audit events. Implementations must never persist user supplied mail content or secrets.</summary>
public interface IAuditRecorder
{
    Task RecordAsync(string eventType, string? actorUserId, string? subjectUserId = null, string? resourceType = null,
        string? resourceId = null, string? detail = null, CancellationToken cancellationToken = default);
}

public interface IAdministrationService : IAuditRecorder
{
    Task<AdministrationSnapshot> GetSnapshotAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task<ServiceResult> QueueSyncAsync(ClaimsPrincipal actor, Guid mailboxId, CancellationToken cancellationToken = default);
    Task<ServiceResult> SetMailboxEnabledAsync(ClaimsPrincipal actor, Guid mailboxId, bool enabled, CancellationToken cancellationToken = default);
    Task<ServiceResult> RetryDeliveryAsync(ClaimsPrincipal actor, Guid deliveryId, CancellationToken cancellationToken = default);
}

public sealed class AdministrationService(MailWinnowDbContext db, IOptions<LocalImapOptions> localImap,
    ILocalImapHealthChecker localImapHealth) : IAdministrationService
{
    public async Task<AdministrationSnapshot> GetSnapshotAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        RequireAdministrator(actor);
        var databaseHealthy = await db.Database.CanConnectAsync(cancellationToken);
        var heartbeat = await db.WorkerHeartbeats.AsNoTracking()
            .OrderBy(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        var mailboxes = await db.SourceMailboxes.AsNoTracking().OrderBy(x => x.DisplayName)
            .Select(x => new AdministrationMailboxStatus(x.Id, x.OwnerUserId, x.DisplayName, x.Enabled, x.PollingStatus, x.LastSyncSucceededUtc, x.SanitizedError)).ToArrayAsync(cancellationToken);
        var audit = await db.AuditEvents.AsNoTracking().OrderByDescending(x => x.OccurredUtc).Take(100).ToArrayAsync(cancellationToken);
        var failures = await db.MessageDeliveries.AsNoTracking().Where(x => x.State == MessageDeliveryState.Failed)
            .OrderBy(x => x.CreatedUtc).Take(100)
            .Select(x => new AdministrationDeliveryFailure(x.Id, x.OwnerUserId, x.LastFailureStage, x.SanitizedError)).ToArrayAsync(cancellationToken);
        var options = localImap.Value;
        var localStatus = await localImapHealth.CheckAsync(options, cancellationToken);
        return new(databaseHealthy, localStatus, heartbeat?.LastSeenUtc,
            await db.SourceMessageHeaders.CountAsync(x => x.EvaluationOutcome == Core.Rules.RuleOutcome.Pending, cancellationToken),
            await db.MessageDeliveries.CountAsync(x => x.State == MessageDeliveryState.Pending || x.State == MessageDeliveryState.RetryPending, cancellationToken),
            await db.MessageDeliveries.CountAsync(x => x.State == MessageDeliveryState.Failed, cancellationToken),
            await db.MessageDeliveries.CountAsync(x => x.State == MessageDeliveryState.Expired && x.LastFailureStage == "Cleanup", cancellationToken), mailboxes, failures, audit);
    }

    public async Task<ServiceResult> QueueSyncAsync(ClaimsPrincipal actor, Guid mailboxId, CancellationToken cancellationToken = default)
    {
        var actorId = RequireAdministrator(actor); var mailbox = await db.SourceMailboxes.SingleOrDefaultAsync(x => x.Id == mailboxId, cancellationToken);
        if (mailbox is null) return ServiceResult.Failure("Source mailbox was not found.");
        mailbox.SyncRequestedUtc = DateTimeOffset.UtcNow; await db.SaveChangesAsync(cancellationToken);
        await RecordAsync("admin.sync.queued", actorId, mailbox.OwnerUserId, "sourceMailbox", mailbox.Id.ToString("N"), cancellationToken: cancellationToken); return ServiceResult.Success();
    }
    public async Task<ServiceResult> SetMailboxEnabledAsync(ClaimsPrincipal actor, Guid mailboxId, bool enabled, CancellationToken cancellationToken = default)
    {
        var actorId = RequireAdministrator(actor); var mailbox = await db.SourceMailboxes.SingleOrDefaultAsync(x => x.Id == mailboxId, cancellationToken);
        if (mailbox is null) return ServiceResult.Failure("Source mailbox was not found.");
        mailbox.Enabled = enabled; await db.SaveChangesAsync(cancellationToken);
        await RecordAsync(enabled ? "admin.mailbox.enabled" : "admin.mailbox.paused", actorId, mailbox.OwnerUserId, "sourceMailbox", mailbox.Id.ToString("N"), cancellationToken: cancellationToken); return ServiceResult.Success();
    }
    public async Task<ServiceResult> RetryDeliveryAsync(ClaimsPrincipal actor, Guid deliveryId, CancellationToken cancellationToken = default)
    {
        var actorId = RequireAdministrator(actor); var delivery = await db.MessageDeliveries.AsNoTracking().SingleOrDefaultAsync(x => x.Id == deliveryId, cancellationToken);
        if (delivery is null) return ServiceResult.Failure("The delivery was not found.");
        var now = DateTimeOffset.UtcNow;
        var changed = await db.MessageDeliveries.Where(x => x.Id == deliveryId && x.State == MessageDeliveryState.Failed).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.State, MessageDeliveryState.RetryPending).SetProperty(x => x.RetryRequestedUtc, now).SetProperty(x => x.RetryRequestedByUserId, actorId)
            .SetProperty(x => x.LastFailureStage, (string?)null).SetProperty(x => x.SanitizedError, (string?)null), cancellationToken);
        if (changed != 1) return ServiceResult.Failure("Only failed deliveries can be retried.");
        await RecordAsync("admin.delivery.retryQueued", actorId, delivery.OwnerUserId, "delivery", deliveryId.ToString("N"), cancellationToken: cancellationToken); return ServiceResult.Success();
    }
    public async Task RecordAsync(string eventType, string? actorUserId, string? subjectUserId = null, string? resourceType = null, string? resourceId = null, string? detail = null, CancellationToken cancellationToken = default)
    {
        // Free-form detail can be a mail body, attachment text, or credential. Retain only operation metadata.
        db.AuditEvents.Add(new AuditEvent { EventType = eventType, ActorUserId = actorUserId, SubjectUserId = subjectUserId, ResourceType = resourceType, ResourceId = resourceId, Detail = null });
        await db.SaveChangesAsync(cancellationToken);
    }
    private static string RequireAdministrator(ClaimsPrincipal actor) => actor.IsInRole(AuthConstants.AdministratorRole)
        ? actor.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new UnauthorizedAccessException() : throw new UnauthorizedAccessException("Administrator access is required.");
}
