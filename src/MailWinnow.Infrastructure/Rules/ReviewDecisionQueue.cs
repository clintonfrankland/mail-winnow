using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MailWinnow.Core.Rules;
using MailWinnow.Infrastructure.Persistence;
using MailWinnow.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MailWinnow.Infrastructure.Rules;

public sealed record ReviewDecisionQueueResult(bool Succeeded, string Message);
public sealed record ReviewDecisionQueueMetrics(int Pending, int Processing, int Retrying, int TerminalFailures, DateTimeOffset? OldestPendingUtc);

public interface IReviewDecisionQueue
{
    Task<ReviewDecisionQueueResult> QueueAsync(ClaimsPrincipal actor, RuleAction action, RuleMatchType matchType,
        string matchValue, int? retentionDays, IReadOnlyList<Guid> messageIds, CancellationToken cancellationToken = default);
    Task<ReviewDecisionQueueMetrics> GetMetricsAsync(CancellationToken cancellationToken = default);
}

/// <summary>Database-backed review command processor. A command is committed before the UI is told it was accepted.</summary>
public sealed class ReviewDecisionQueue(IServiceScopeFactory scopes, ILogger<ReviewDecisionQueue> logger)
    : BackgroundService, IReviewDecisionQueue
{
    public async Task<ReviewDecisionQueueResult> QueueAsync(ClaimsPrincipal actor, RuleAction action, RuleMatchType matchType,
        string matchValue, int? retentionDays, IReadOnlyList<Guid> messageIds, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var ownerId = scope.ServiceProvider.GetRequiredService<IOwnershipAuthorizer>().RequireCurrentUserId(actor);
        var normalizedValue = matchValue.Trim();
        if (action is not (RuleAction.PermanentlyAllow or RuleAction.PermanentlyBlock or RuleAction.DeleteOneMessage)) return new(false, "That review decision is not supported.");
        if (action != RuleAction.DeleteOneMessage && matchType is not (RuleMatchType.ExactSender or RuleMatchType.SenderDomain)) return new(false, "Only sender and domain decisions can be queued from review.");
        if (action == RuleAction.DeleteOneMessage && messageIds.Count == 0) return new(false, "No messages were selected.");
        if (action != RuleAction.DeleteOneMessage && string.IsNullOrWhiteSpace(normalizedValue)) return new(false, "The sender or domain is required.");
        if (retentionDays is { } days && days is not (30 or 7 or 3 or 1)) return new(false, "Destination retention must be Forever, 1 month, 1 week, 3 days, or 1 day.");

        var ids = messageIds.Distinct().Order().ToArray();
        var key = CreateIdempotencyKey(action, matchType, normalizedValue, action == RuleAction.PermanentlyAllow ? retentionDays : null, ids);
        var db = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
        if (await db.ReviewDecisionWorkItems.AnyAsync(x => x.OwnerUserId == ownerId && x.IdempotencyKey == key &&
                (x.Status == ReviewDecisionWorkStatus.Pending || x.Status == ReviewDecisionWorkStatus.Processing || x.Status == ReviewDecisionWorkStatus.Retrying), cancellationToken))
            return new(true, "Review decision was already accepted.");
        db.ReviewDecisionWorkItems.Add(new ReviewDecisionWorkItem
        {
            OwnerUserId = ownerId, Action = action, MatchType = matchType, MatchValue = normalizedValue,
            RetentionDays = action == RuleAction.PermanentlyAllow ? retentionDays : null,
            MessageIdsJson = JsonSerializer.Serialize(ids), IdempotencyKey = key, Status = ReviewDecisionWorkStatus.Pending,
            NextAttemptUtc = DateTimeOffset.UtcNow
        });
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException)
        {
            // The filtered unique owner/key index is the final active duplicate-click guard. A concurrent accepted command is still success to the caller.
            db.ChangeTracker.Clear();
            if (!await db.ReviewDecisionWorkItems.AnyAsync(x => x.OwnerUserId == ownerId && x.IdempotencyKey == key &&
                    (x.Status == ReviewDecisionWorkStatus.Pending || x.Status == ReviewDecisionWorkStatus.Processing || x.Status == ReviewDecisionWorkStatus.Retrying), cancellationToken)) throw;
        }
        return new(true, "Review decision was accepted.");
    }

    public async Task<ReviewDecisionQueueMetrics> GetMetricsAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
        var items = db.ReviewDecisionWorkItems.AsNoTracking();
        var pending = items.Where(x => x.Status == ReviewDecisionWorkStatus.Pending || x.Status == ReviewDecisionWorkStatus.Retrying);
        var oldestPendingUtc = db.Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite"
            ? await db.ReviewDecisionWorkItems.FromSqlInterpolated($"SELECT * FROM ReviewDecisionWorkItems WHERE Status IN ({ReviewDecisionWorkStatus.Pending.ToString()}, {ReviewDecisionWorkStatus.Retrying.ToString()}) ORDER BY julianday(CreatedUtc) LIMIT 1")
                .AsNoTracking().Select(x => (DateTimeOffset?)x.CreatedUtc).SingleOrDefaultAsync(cancellationToken)
            : await pending.OrderBy(x => x.CreatedUtc).Select(x => (DateTimeOffset?)x.CreatedUtc).FirstOrDefaultAsync(cancellationToken);
        return new(await items.CountAsync(x => x.Status == ReviewDecisionWorkStatus.Pending, cancellationToken),
            await items.CountAsync(x => x.Status == ReviewDecisionWorkStatus.Processing, cancellationToken),
            await items.CountAsync(x => x.Status == ReviewDecisionWorkStatus.Retrying, cancellationToken),
            await items.CountAsync(x => x.Status == ReviewDecisionWorkStatus.Failed, cancellationToken),
            oldestPendingUtc);
    }

    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RenewalInterval = TimeSpan.FromSeconds(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var idleDelay = TimeSpan.FromMilliseconds(250);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await ProcessBatchAsync(stoppingToken))
                {
                    idleDelay = TimeSpan.FromMilliseconds(250);
                    continue;
                }
                await Task.Delay(idleDelay, stoppingToken);
                idleDelay = TimeSpan.FromMilliseconds(Math.Min(idleDelay.TotalMilliseconds * 2, 2000));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Review decision worker loop failed");
                try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }

    private async Task<bool> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        List<Guid> ids;
        await using (var selectionScope = scopes.CreateAsyncScope())
        {
            var database = selectionScope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
            var now = DateTimeOffset.UtcNow;
            var staleBefore = now - LeaseDuration;
            // NextAttemptUtc is the renewable expiry while Processing. StartedUtc remains the
            // original start time; the extra start bound also supports rows from the old processor.
            var staleQuery = database.Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite"
                ? database.ReviewDecisionWorkItems.FromSqlInterpolated($"SELECT * FROM ReviewDecisionWorkItems WHERE Status = {ReviewDecisionWorkStatus.Processing.ToString()} AND julianday(StartedUtc) < julianday({staleBefore}) AND julianday(NextAttemptUtc) <= julianday({now}) ORDER BY julianday(NextAttemptUtc) LIMIT 16")
                : database.ReviewDecisionWorkItems.Where(x => x.Status == ReviewDecisionWorkStatus.Processing && x.StartedUtc < staleBefore && x.NextAttemptUtc <= now)
                    .OrderBy(x => x.NextAttemptUtc).Take(16);
            foreach (var expired in await staleQuery.AsNoTracking().ToListAsync(cancellationToken))
            {
                // A heartbeat or another recovery between selection and update invalidates this CAS.
                await database.ReviewDecisionWorkItems.Where(x => x.Id == expired.Id && x.Status == ReviewDecisionWorkStatus.Processing &&
                        x.AttemptCount == expired.AttemptCount && x.NextAttemptUtc == expired.NextAttemptUtc)
                    .ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, ReviewDecisionWorkStatus.Retrying)
                        .SetProperty(x => x.NextAttemptUtc, now).SetProperty(x => x.StartedUtc, (DateTimeOffset?)null), cancellationToken);
            }
            var eligible = database.Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite"
                ? database.ReviewDecisionWorkItems.FromSqlInterpolated($"SELECT * FROM ReviewDecisionWorkItems WHERE Status IN ({ReviewDecisionWorkStatus.Pending.ToString()}, {ReviewDecisionWorkStatus.Retrying.ToString()}) AND julianday(NextAttemptUtc) <= julianday({now}) ORDER BY julianday(CreatedUtc), CreatedUtc, Id LIMIT 16")
                : database.ReviewDecisionWorkItems.Where(x => (x.Status == ReviewDecisionWorkStatus.Pending || x.Status == ReviewDecisionWorkStatus.Retrying) && x.NextAttemptUtc <= now)
                    .OrderBy(x => x.CreatedUtc).ThenBy(x => x.Id).Take(16);
            ids = await eligible.AsNoTracking().Select(x => x.Id).ToListAsync(cancellationToken);
        }
        foreach (var id in ids) await ProcessOneAsync(id, cancellationToken);
        return ids.Count > 0;
    }

    private async Task ProcessOneAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
        // A queued selection may have been retried, claimed, or rescheduled while earlier work ran.
        var selected = await database.ReviewDecisionWorkItems.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (selected is null || selected.Status is not (ReviewDecisionWorkStatus.Pending or ReviewDecisionWorkStatus.Retrying) || selected.NextAttemptUtc > DateTimeOffset.UtcNow) return;
        var now = DateTimeOffset.UtcNow;
        var claimed = await database.ReviewDecisionWorkItems.Where(x => x.Id == id && x.Status == selected.Status &&
                x.AttemptCount == selected.AttemptCount && x.NextAttemptUtc == selected.NextAttemptUtc)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, ReviewDecisionWorkStatus.Processing)
                .SetProperty(x => x.StartedUtc, now).SetProperty(x => x.NextAttemptUtc, now + LeaseDuration)
                .SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1), cancellationToken);
        if (claimed == 0) return;
        var attempt = selected.AttemptCount + 1;
        using var processing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeat = RenewLeaseAsync(id, attempt, processing, heartbeatStop.Token);
        try
        {
            var rules = scope.ServiceProvider.GetRequiredService<IRuleManagementService>();
            if (selected.Action == RuleAction.DeleteOneMessage)
                foreach (var messageId in JsonSerializer.Deserialize<Guid[]>(selected.MessageIdsJson) ?? [])
                {
                    processing.Token.ThrowIfCancellationRequested();
                    await rules.SetMessageDecisionAsync(new MessageDecision { OwnerUserId = selected.OwnerUserId, SourceMessageHeaderId = messageId, Action = RuleAction.DeleteOneMessage }, processing.Token);
                }
            else
            {
                var existing = await database.MailRules.AsNoTracking().FirstOrDefaultAsync(x => x.OwnerUserId == selected.OwnerUserId && x.Scope == RuleScope.User && x.MatchType == selected.MatchType && x.MatchValue.ToLower() == selected.MatchValue.ToLower(), processing.Token);
                await rules.PersistAsync(new MailRule { Id = existing?.Id ?? selected.Id, OwnerUserId = selected.OwnerUserId, Action = selected.Action, Scope = RuleScope.User, MatchType = selected.MatchType, MatchValue = selected.MatchValue, DeliveredMessageRetentionDays = selected.RetentionDays }, processing.Token);
                logger.LogInformation("Review decision {WorkItemId} attempt {Attempt}: rule persisted; reevaluation starting", id, attempt);
                // Exactly one replay-safe pass, including when a crash occurred after rule persistence.
                await scope.ServiceProvider.GetRequiredService<IRuleEvaluationService>()
                    .ReevaluateOwnedHeadersAsync(selected.OwnerUserId, processing.Token);
                logger.LogInformation("Review decision {WorkItemId} attempt {Attempt}: reevaluation finished", id, attempt);
            }
            processing.Token.ThrowIfCancellationRequested();
            var completed = await database.ReviewDecisionWorkItems.Where(x => x.Id == id && x.Status == ReviewDecisionWorkStatus.Processing && x.AttemptCount == attempt)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, ReviewDecisionWorkStatus.Completed)
                    .SetProperty(x => x.CompletedUtc, DateTimeOffset.UtcNow).SetProperty(x => x.LastError, (string?)null), cancellationToken);
            if (completed == 0) logger.LogWarning("Review decision {WorkItemId} attempt {Attempt}: completion ignored after ownership changed", id, attempt);
        }
        catch (OperationCanceledException) when (processing.IsCancellationRequested)
        {
            // Shutdown or lost ownership: leave the durable lease to expire, never acknowledge it.
        }
        catch (Exception ex)
        {
            if (!processing.IsCancellationRequested)
                await database.ReviewDecisionWorkItems.Where(x => x.Id == id && x.Status == ReviewDecisionWorkStatus.Processing && x.AttemptCount == attempt)
                    .ExecuteUpdateAsync(update => update.SetProperty(x => x.LastError, "Processing failed; retry is scheduled.")
                        .SetProperty(x => x.StartedUtc, (DateTimeOffset?)null)
                        .SetProperty(x => x.Status, attempt >= 3 ? ReviewDecisionWorkStatus.Failed : ReviewDecisionWorkStatus.Retrying)
                        .SetProperty(x => x.NextAttemptUtc, DateTimeOffset.UtcNow.AddSeconds(Math.Pow(2, attempt))), cancellationToken);
            logger.LogWarning(ex, "Review decision work item {WorkItemId} failed on attempt {Attempt}", id, attempt);
        }
        finally
        {
            await heartbeatStop.CancelAsync();
            await heartbeat;
        }
    }

    private async Task RenewLeaseAsync(Guid id, int attempt, CancellationTokenSource processing, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(RenewalInterval, cancellationToken);
                await using var scope = scopes.CreateAsyncScope();
                var database = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
                var renewed = await database.ReviewDecisionWorkItems.Where(x => x.Id == id && x.Status == ReviewDecisionWorkStatus.Processing && x.AttemptCount == attempt)
                    .ExecuteUpdateAsync(update => update.SetProperty(x => x.NextAttemptUtc, DateTimeOffset.UtcNow + LeaseDuration), cancellationToken);
                if (renewed == 0)
                {
                    await processing.CancelAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Review decision lease renewal failed for {WorkItemId}; stopping this attempt", id);
            await processing.CancelAsync();
        }
    }

    private static string CreateIdempotencyKey(RuleAction action, RuleMatchType type, string value, int? retention, IEnumerable<Guid> ids)
    {
        var input = $"{action}|{type}|{value.ToUpperInvariant()}|{retention}|{string.Join(',', ids)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }
}
