using System.Security.Claims;
using MailWinnow.Infrastructure.Persistence;
using MailWinnow.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MailWinnow.Infrastructure.Mailboxes;

public sealed record InboxRefreshRequest(bool Succeeded, string Message, DateTimeOffset RequestedUtc, int MailboxCount);

public sealed record InboxRefreshStatus(int WaitingSources, int SyncingSources, int FailedSources,
    int PendingDeliveries, int FailedDeliveries, DateTimeOffset? LatestInboxChangeUtc)
{
    public bool IsComplete => WaitingSources == 0 && SyncingSources == 0 && PendingDeliveries == 0;
}

public interface IInboxRefreshService
{
    Task<InboxRefreshRequest> RequestAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task<InboxRefreshStatus> GetStatusAsync(ClaimsPrincipal actor, DateTimeOffset requestedUtc, CancellationToken cancellationToken = default);
}

/// <summary>Enqueues owned source refreshes atomically; each operation uses a fresh scope and never contacts IMAP.</summary>
public sealed class InboxRefreshService(IServiceScopeFactory scopes) : IInboxRefreshService
{
    public async Task<InboxRefreshRequest> RequestAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var owner = scope.ServiceProvider.GetRequiredService<IOwnershipAuthorizer>().RequireCurrentUserId(actor);
        var db = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
        db.Database.SetCommandTimeout(10);
        var requestedUtc = DateTimeOffset.UtcNow;
        // One UPDATE is the durable acceptance boundary. Repeated clicks coalesce in the existing marker.
        var count = await UpdateRequestMarkersAsync(db, owner, requestedUtc, cancellationToken);
        return new(true, count == 0 ? "No enabled source accounts to check." : "Checking source accounts in the background.", requestedUtc, count);
    }

    internal static Task<int> UpdateRequestMarkersAsync(MailWinnowDbContext db, string owner,
        DateTimeOffset requestedUtc, CancellationToken cancellationToken = default)
    {
        // Concurrent clicks may reach SQL in reverse timestamp order; never move a pending marker backwards.
        if (db.Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite")
            return db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE SourceMailboxes SET SyncRequestedUtc = CASE
                    WHEN julianday(SyncRequestedUtc) > julianday({requestedUtc}) THEN SyncRequestedUtc ELSE {requestedUtc} END
                WHERE Enabled = 1 AND OwnerUserId = {owner}
                """, cancellationToken);
        return db.SourceMailboxes.Where(source => source.Enabled && source.OwnerUserId == owner)
            .ExecuteUpdateAsync(update => update.SetProperty(source => source.SyncRequestedUtc,
                source => source.SyncRequestedUtc > requestedUtc ? source.SyncRequestedUtc : requestedUtc), cancellationToken);
    }

    public async Task<InboxRefreshStatus> GetStatusAsync(ClaimsPrincipal actor, DateTimeOffset requestedUtc, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var owner = scope.ServiceProvider.GetRequiredService<IOwnershipAuthorizer>().RequireCurrentUserId(actor);
        var db = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
        db.Database.SetCommandTimeout(10);
        // Only small source-account state rows are materialized, never catalogue headers or deliveries.
        var sources = await db.SourceMailboxes.AsNoTracking().Where(source => source.Enabled && source.OwnerUserId == owner)
            .Select(source => new { source.SyncRequestedUtc, source.LastSyncAttemptUtc, source.PollingStatus })
            .ToArrayAsync(cancellationToken);
        // Include current owned background work even if another tab or a scheduled pass started it.
        var waiting = sources.Count(source => source.SyncRequestedUtc != null);
        var syncing = sources.Count(source => source.PollingStatus == "Synchronizing");
        var failed = sources.Count(source => source.SyncRequestedUtc == null && source.PollingStatus == "Failed" && source.LastSyncAttemptUtc >= requestedUtc);

        var deliveries = db.MessageDeliveries.AsNoTracking().Where(delivery => delivery.OwnerUserId == owner &&
            db.SourceMessageHeaders.Any(header => header.Id == delivery.SourceMessageHeaderId &&
                db.SourceMailboxes.Any(source => source.Id == header.SourceMailboxId && source.Enabled && source.OwnerUserId == owner)));
        var pending = await deliveries.CountAsync(delivery => delivery.State == MessageDeliveryState.Pending ||
            delivery.State == MessageDeliveryState.Fetching || delivery.State == MessageDeliveryState.Delivering ||
            delivery.State == MessageDeliveryState.RetryPending, cancellationToken);
        int failedDeliveries;
        DateTimeOffset? latestChange;
        if (db.Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite")
        {
            // SQLite cannot compare/aggregate DateTimeOffset in translated LINQ. Keep this scalar on the server too.
            failedDeliveries = await db.Database.SqlQuery<int>($"""
                SELECT COUNT(*) AS Value FROM MessageDeliveries AS delivery
                JOIN SourceMessageHeaders AS header ON header.Id = delivery.SourceMessageHeaderId
                JOIN SourceMailboxes AS source ON source.Id = header.SourceMailboxId
                WHERE delivery.OwnerUserId = {owner} AND source.OwnerUserId = {owner} AND source.Enabled = 1
                AND delivery.State = 'Failed' AND
                (julianday(delivery.CreatedUtc) >= julianday({requestedUtc}) OR
                 julianday(delivery.FetchStartedUtc) >= julianday({requestedUtc}) OR
                 julianday(delivery.DeliveryStartedUtc) >= julianday({requestedUtc}) OR
                 julianday(delivery.RetryRequestedUtc) >= julianday({requestedUtc}))
                """).SingleAsync(cancellationToken);
            latestChange = await db.Database.SqlQuery<DateTimeOffset?>($"""
                SELECT MAX(CASE WHEN DeliveredUtc IS NULL OR julianday(DeletedUtc) > julianday(DeliveredUtc)
                    THEN DeletedUtc ELSE DeliveredUtc END) AS Value
                FROM MessageDeliveries WHERE OwnerUserId = {owner}
                """).SingleAsync(cancellationToken);
        }
        else
        {
            failedDeliveries = await deliveries.CountAsync(delivery => delivery.State == MessageDeliveryState.Failed &&
                (delivery.CreatedUtc >= requestedUtc || delivery.FetchStartedUtc >= requestedUtc ||
                 delivery.DeliveryStartedUtc >= requestedUtc || delivery.RetryRequestedUtc >= requestedUtc), cancellationToken);
            latestChange = await db.MessageDeliveries.Where(delivery => delivery.OwnerUserId == owner)
                .Select(delivery => delivery.DeliveredUtc == null || delivery.DeletedUtc > delivery.DeliveredUtc
                    ? delivery.DeletedUtc : delivery.DeliveredUtc).MaxAsync(cancellationToken);
        }
        return new(waiting, syncing, failed, pending, failedDeliveries, latestChange);
    }
}

/// <summary>Lightweight durable-marker wakeup. Does not synchronize or start a full cycle while idle.</summary>
public sealed class MailSyncWakeSignal(IServiceScopeFactory scopes, ILogger<MailSyncWakeSignal>? logger = null)
{
    public async Task<bool> HasPendingRequestAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
        db.Database.SetCommandTimeout(10);
        return await db.SourceMailboxes.AsNoTracking().AnyAsync(source => source.Enabled && source.SyncRequestedUtc != null, cancellationToken);
    }

    public Task WaitAsync(TimeSpan scheduledInterval, CancellationToken cancellationToken = default) =>
        WaitForRequestAsync(scheduledInterval, HasPendingRequestAsync,
            (delay, token) => Task.Delay(delay, token), cancellationToken,
            () => logger?.LogWarning("Could not check for manual refresh requests; retaining the normal scheduled polling interval."));

    internal static async Task WaitForRequestAsync(TimeSpan scheduledInterval,
        Func<CancellationToken, Task<bool>> hasPendingRequest, Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken cancellationToken, Action? onProbeFailure = null)
    {
        // Count waited intervals, so a requested cycle never shortens the normal polling interval while idle.
        var remaining = scheduledInterval;
        while (remaining > TimeSpan.Zero)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pause = remaining < TimeSpan.FromSeconds(2) ? remaining : TimeSpan.FromSeconds(2);
            await delay(pause, cancellationToken);
            remaining -= pause;
            try
            {
                if (await hasPendingRequest(cancellationToken)) return;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // A transient database outage must not terminate the BackgroundService or create a retry storm.
                onProbeFailure?.Invoke();
                if (remaining > TimeSpan.Zero) await delay(remaining, cancellationToken);
                return;
            }
        }
    }
}
