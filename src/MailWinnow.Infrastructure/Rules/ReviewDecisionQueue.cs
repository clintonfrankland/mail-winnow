using System.Security.Claims;
using System.Threading.Channels;
using MailWinnow.Core.Rules;
using MailWinnow.Infrastructure.Persistence;
using MailWinnow.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MailWinnow.Infrastructure.Rules;

public sealed record ReviewDecisionQueueResult(bool Succeeded, string Message);

public interface IReviewDecisionQueue
{
    Task<ReviewDecisionQueueResult> QueueAsync(ClaimsPrincipal actor, RuleAction action, RuleMatchType matchType,
        string matchValue, int? retentionDays, IReadOnlyList<Guid> messageIds, CancellationToken cancellationToken = default);
}

public sealed class ReviewDecisionQueue(IServiceScopeFactory scopes, ILogger<ReviewDecisionQueue> logger)
    : BackgroundService, IReviewDecisionQueue
{
    private readonly Channel<QueuedReviewDecision> _queue = Channel.CreateUnbounded<QueuedReviewDecision>(new()
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });

    public async Task<ReviewDecisionQueueResult> QueueAsync(ClaimsPrincipal actor, RuleAction action, RuleMatchType matchType,
        string matchValue, int? retentionDays, IReadOnlyList<Guid> messageIds, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var ownerId = scope.ServiceProvider.GetRequiredService<IOwnershipAuthorizer>().RequireCurrentUserId(actor);
        var normalizedValue = matchValue.Trim();
        if (action is not (RuleAction.PermanentlyAllow or RuleAction.PermanentlyBlock or RuleAction.DeleteOneMessage))
            return new(false, "That review decision is not supported.");
        if (action != RuleAction.DeleteOneMessage && matchType is not (RuleMatchType.ExactSender or RuleMatchType.SenderDomain))
            return new(false, "Only sender and domain decisions can be queued from review.");
        if (action == RuleAction.DeleteOneMessage && messageIds.Count == 0)
            return new(false, "No messages were selected.");
        if (action != RuleAction.DeleteOneMessage && string.IsNullOrWhiteSpace(normalizedValue))
            return new(false, "The sender or domain is required.");
        if (retentionDays is { } days && days is not (30 or 7 or 3 or 1))
            return new(false, "Destination retention must be Forever, 1 month, 1 week, 3 days, or 1 day.");

        await _queue.Writer.WriteAsync(new(Guid.NewGuid(), ownerId, action, matchType, normalizedValue,
            action == RuleAction.PermanentlyAllow ? retentionDays : null, messageIds.Distinct().ToArray()), cancellationToken);
        return new(true, "Review decision was queued.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var decision in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ApplyAsync(decision, stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Queued review decision {DecisionId} failed on attempt {Attempt}", decision.Id, decision.Attempt);
                if (decision.Attempt < MaximumAttempts)
                    _queue.Writer.TryWrite(decision with { Attempt = decision.Attempt + 1 });
            }
        }
    }

    private async Task ApplyAsync(QueuedReviewDecision decision, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var rules = scope.ServiceProvider.GetRequiredService<IRuleManagementService>();
        if (decision.Action == RuleAction.DeleteOneMessage)
        {
            foreach (var messageId in decision.MessageIds)
                await rules.SetMessageDecisionAsync(new MessageDecision
                {
                    OwnerUserId = decision.OwnerUserId,
                    SourceMessageHeaderId = messageId,
                    Action = RuleAction.DeleteOneMessage
                }, cancellationToken);
            return;
        }

        var db = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
        var matchingRules = await db.MailRules.AsNoTracking().Where(x => x.OwnerUserId == decision.OwnerUserId &&
            x.Scope == RuleScope.User && x.MatchType == decision.MatchType).ToListAsync(cancellationToken);
        var existing = matchingRules.FirstOrDefault(x => string.Equals(x.MatchValue, decision.MatchValue, StringComparison.OrdinalIgnoreCase));
        await rules.AddOrUpdateAsync(new MailRule
        {
            Id = existing?.Id ?? decision.Id,
            OwnerUserId = decision.OwnerUserId,
            Action = decision.Action,
            Scope = RuleScope.User,
            MatchType = decision.MatchType,
            MatchValue = decision.MatchValue,
            DeliveredMessageRetentionDays = decision.RetentionDays
        }, cancellationToken);
    }

    private const int MaximumAttempts = 3;
    private sealed record QueuedReviewDecision(Guid Id, string OwnerUserId, RuleAction Action, RuleMatchType MatchType,
        string MatchValue, int? RetentionDays, IReadOnlyList<Guid> MessageIds, int Attempt = 1);
}
