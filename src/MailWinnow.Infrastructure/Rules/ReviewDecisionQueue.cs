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
        // SQLite cannot aggregate or sort DateTimeOffset.  The operational queue is bounded by
        // pending/retrying commands, so calculate this presentation-only metric client-side.
        var oldestPendingUtc = (await items.Where(x => x.Status == ReviewDecisionWorkStatus.Pending || x.Status == ReviewDecisionWorkStatus.Retrying)
            .Select(x => x.CreatedUtc).ToListAsync(cancellationToken)).DefaultIfEmpty().Min();
        return new(await items.CountAsync(x => x.Status == ReviewDecisionWorkStatus.Pending, cancellationToken),
            await items.CountAsync(x => x.Status == ReviewDecisionWorkStatus.Processing, cancellationToken),
            await items.CountAsync(x => x.Status == ReviewDecisionWorkStatus.Retrying, cancellationToken),
            await items.CountAsync(x => x.Status == ReviewDecisionWorkStatus.Failed, cancellationToken),
            oldestPendingUtc == default ? null : oldestPendingUtc);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var didWork = await ProcessBatchAsync(stoppingToken);
                if (!didWork) await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { logger.LogError(ex, "Review decision worker loop failed"); await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
        }
    }

    private async Task<bool> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        await using var selectionScope = scopes.CreateAsyncScope();
        var selectionDb = selectionScope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
        var now = DateTimeOffset.UtcNow;
        // A killed container can leave a leased row behind. Releasing only stale leases prevents concurrent workers from duplicating active work.
        var stale = (await selectionDb.ReviewDecisionWorkItems.Where(x => x.Status == ReviewDecisionWorkStatus.Processing).ToListAsync(cancellationToken))
            .Where(x => x.StartedUtc < now.AddMinutes(-2)).ToList();
        foreach (var item in stale) { item.Status = ReviewDecisionWorkStatus.Retrying; item.NextAttemptUtc = now; item.StartedUtc = null; }
        if (stale.Count > 0) await selectionDb.SaveChangesAsync(cancellationToken);
        var ids = (await selectionDb.ReviewDecisionWorkItems.AsNoTracking().Where(x =>
                x.Status == ReviewDecisionWorkStatus.Pending || x.Status == ReviewDecisionWorkStatus.Retrying).ToListAsync(cancellationToken))
            .Where(x => x.NextAttemptUtc <= now).OrderBy(x => x.CreatedUtc).Take(16).Select(x => x.Id).ToList();
        foreach (var id in ids) await ProcessOneAsync(id, cancellationToken);
        return ids.Count > 0;
    }

    private async Task ProcessOneAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
        var now = DateTimeOffset.UtcNow;
        var claimed = await db.ReviewDecisionWorkItems.Where(x => x.Id == id &&
                (x.Status == ReviewDecisionWorkStatus.Pending || x.Status == ReviewDecisionWorkStatus.Retrying))
            .ExecuteUpdateAsync(x => x.SetProperty(i => i.Status, ReviewDecisionWorkStatus.Processing)
                .SetProperty(i => i.StartedUtc, now).SetProperty(i => i.AttemptCount, i => i.AttemptCount + 1), cancellationToken);
        if (claimed == 0) return;
        var item = await db.ReviewDecisionWorkItems.SingleAsync(x => x.Id == id, cancellationToken);
        try
        {
            var rules = scope.ServiceProvider.GetRequiredService<IRuleManagementService>();
            if (item.Action == RuleAction.DeleteOneMessage)
                foreach (var messageId in JsonSerializer.Deserialize<Guid[]>(item.MessageIdsJson) ?? [])
                    await rules.SetMessageDecisionAsync(new MessageDecision { OwnerUserId = item.OwnerUserId, SourceMessageHeaderId = messageId, Action = RuleAction.DeleteOneMessage }, cancellationToken);
            else
            {
                var existing = await db.MailRules.AsNoTracking().FirstOrDefaultAsync(x => x.OwnerUserId == item.OwnerUserId && x.Scope == RuleScope.User && x.MatchType == item.MatchType && x.MatchValue.ToLower() == item.MatchValue.ToLower(), cancellationToken);
                await rules.AddOrUpdateAsync(new MailRule { Id = existing?.Id ?? item.Id, OwnerUserId = item.OwnerUserId, Action = item.Action, Scope = RuleScope.User, MatchType = item.MatchType, MatchValue = item.MatchValue, DeliveredMessageRetentionDays = item.RetentionDays }, cancellationToken);

                // AddOrUpdateAsync intentionally returns early for an identical persisted rule.  That is
                // normally useful, but a process can die after persisting that rule and before its
                // previous catalog pass finishes.  The work item's completion boundary therefore owns
                // a replay-safe full pass: retries always finish the pass before acknowledging work.
                // Evaluation and delivery are idempotent, so redoing a completed pass is safe too.
                await scope.ServiceProvider.GetRequiredService<IRuleEvaluationService>()
                    .ReevaluateOwnedHeadersAsync(item.OwnerUserId, cancellationToken);
            }
            item.Status = ReviewDecisionWorkStatus.Completed; item.CompletedUtc = DateTimeOffset.UtcNow; item.LastError = null;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            item.LastError = "Processing failed; retry is scheduled.";
            item.StartedUtc = null;
            item.Status = item.AttemptCount >= 3 ? ReviewDecisionWorkStatus.Failed : ReviewDecisionWorkStatus.Retrying;
            item.NextAttemptUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Pow(2, item.AttemptCount));
            await db.SaveChangesAsync(cancellationToken);
            logger.LogWarning(ex, "Review decision work item {WorkItemId} failed on attempt {Attempt}", item.Id, item.AttemptCount);
        }
    }

    private static string CreateIdempotencyKey(RuleAction action, RuleMatchType type, string value, int? retention, IEnumerable<Guid> ids)
    {
        var input = $"{action}|{type}|{value.ToUpperInvariant()}|{retention}|{string.Join(',', ids)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }
}
