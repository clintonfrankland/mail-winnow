using MailWinnow.Core.Rules;
using MailWinnow.Infrastructure.Mailboxes;
using MailWinnow.Infrastructure.Persistence;
using MailWinnow.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace MailWinnow.Infrastructure.Rules;

public sealed record MessageReviewFilter(Guid? SourceMailboxId, RuleOutcome? Outcome, string? Search);
public sealed record MessageReviewItem(Guid Id, string Sender, string Subject, string Account, DateTimeOffset ReceivedUtc,
    string SourceStatus, RuleOutcome Outcome, string DeliveryStatus, string RuleContext, string RetentionContext, Guid? DeliveryId = null,
    Guid SourceMailboxId = default, string FolderName = "", uint Uid = 0, uint UidValidity = 0);
public sealed record MessageReviewGroup(string Value, int Count, DateTimeOffset MostRecentUtc, IReadOnlyList<Guid> MessageIds,
    IReadOnlyList<string> Senders, IReadOnlyList<string> Samples, IReadOnlyList<string> RuleContexts);
public sealed record ReviewRule(Guid Id, RuleAction Action, RuleScope Scope, RuleMatchType MatchType, string MatchValue,
    Guid? SourceMailboxId, DateTimeOffset? ExpiresUtc, int? RetentionDays, DateTimeOffset CreatedUtc);

public interface IMessageReviewService
{
    Task<IReadOnlyList<MessageReviewItem>> GetRecentAsync(ClaimsPrincipal user, MessageReviewFilter filter, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MessageReviewGroup>> GetBySenderAsync(ClaimsPrincipal user, MessageReviewFilter filter, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MessageReviewGroup>> GetBySubjectAsync(ClaimsPrincipal user, MessageReviewFilter filter, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReviewRule>> GetRulesAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default);
}

/// <summary>Read model for message review. Every query is constrained to the signed-in owner's source accounts.</summary>
public sealed class MessageReviewService(MailWinnowDbContext db, IOwnershipAuthorizer ownership) : IMessageReviewService
{
    public async Task<IReadOnlyList<MessageReviewItem>> GetRecentAsync(ClaimsPrincipal user, MessageReviewFilter filter, CancellationToken cancellationToken = default)
    {
        var owner = ownership.RequireCurrentUserId(user);
        var sourceQuery = db.SourceMailboxes.AsNoTracking().Where(x => x.OwnerUserId == owner);
        if (filter.SourceMailboxId is { } sourceId) sourceQuery = sourceQuery.Where(x => x.Id == sourceId);
        var sources = await sourceQuery.ToDictionaryAsync(x => x.Id, cancellationToken);
        // Order after materialization because SQLite (used by the service tests) cannot order DateTimeOffset values.
        var headers = (await ReviewProjectionQueries.WithoutQueuedMessages(db, owner, db.SourceMessageHeaders.AsNoTracking().Where(x => sourceQuery.Select(source => source.Id).Contains(x.SourceMailboxId))).ToListAsync(cancellationToken))
            .OrderByDescending(x => x.ReceivedUtc).ToList();
        var decisions = await db.MessageDecisions.AsNoTracking().Where(x => x.OwnerUserId == owner).ToDictionaryAsync(x => x.SourceMessageHeaderId, cancellationToken);
        var rules = await db.MailRules.AsNoTracking().Where(x => x.OwnerUserId == owner).ToListAsync(cancellationToken);
        var deliveries = await db.MessageDeliveries.AsNoTracking().Where(x => x.OwnerUserId == owner).ToDictionaryAsync(x => x.SourceMessageHeaderId, cancellationToken);
        var compiledRules = RuleEvaluator.Compile(rules.Select(x => new RuleCandidate(x.Id, x.Action, x.Scope, x.MatchType, x.MatchValue, x.SourceMailboxId, x.EffectiveUtc, x.ExpiresUtc)));
        var rulesById = rules.ToDictionary(x => x.Id);
        var now = DateTimeOffset.UtcNow;
        // Message Review is an inbox for unresolved decisions, not a history view.
        // Evaluate against the current rules so expired or edited rules are reflected immediately.
        var items = headers.Select(header => ToItem(header, sources[header.SourceMailboxId], compiledRules, rulesById, decisions.GetValueOrDefault(header.Id), deliveries.GetValueOrDefault(header.Id), now))
            .Where(x => x.Outcome == RuleOutcome.Pending);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var needle = filter.Search.Trim();
            items = items.Where(x => x.Sender.Contains(needle, StringComparison.OrdinalIgnoreCase) || x.Subject.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }
        return items.ToList();
    }

    public async Task<IReadOnlyList<MessageReviewGroup>> GetBySenderAsync(ClaimsPrincipal user, MessageReviewFilter filter, CancellationToken cancellationToken = default) =>
        GroupAsync(await GetRecentAsync(user, filter, cancellationToken), x => x.Sender);

    public async Task<IReadOnlyList<MessageReviewGroup>> GetBySubjectAsync(ClaimsPrincipal user, MessageReviewFilter filter, CancellationToken cancellationToken = default) =>
        GroupAsync(await GetRecentAsync(user, filter, cancellationToken), x => x.Subject);

    public async Task<IReadOnlyList<ReviewRule>> GetRulesAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        var owner = ownership.RequireCurrentUserId(user);
        return await db.MailRules.AsNoTracking().Where(x => x.OwnerUserId == owner).OrderByDescending(x => x.CreatedUtc)
            .Select(x => new ReviewRule(x.Id, x.Action, x.Scope, x.MatchType, x.MatchValue, x.SourceMailboxId,
                x.ExpiresUtc, x.DeliveredMessageRetentionDays, x.CreatedUtc)).ToListAsync(cancellationToken);
    }

    private static IReadOnlyList<MessageReviewGroup> GroupAsync(IReadOnlyList<MessageReviewItem> items, Func<MessageReviewItem, string> key) => items
        .GroupBy(key).OrderByDescending(x => x.Count()).ThenByDescending(x => x.Max(i => i.ReceivedUtc)).Select(group => new MessageReviewGroup(
            group.Key, group.Count(), group.Max(x => x.ReceivedUtc), group.Select(x => x.Id).ToList(),
            group.Select(x => x.Sender).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList(),
            group.OrderByDescending(x => x.ReceivedUtc).Take(3).Select(x => x.Subject).ToList(),
            group.Select(x => x.RuleContext).Distinct().Take(3).ToList())).ToList();

    private static MessageReviewItem ToItem(SourceMessageHeader header, SourceMailbox source, RuleEvaluator.CompiledRuleSet compiledRules, IReadOnlyDictionary<Guid, MailRule> rulesById, MessageDecision? decision, MessageDelivery? delivery, DateTimeOffset now)
    {
        var evaluation = compiledRules.Evaluate(header.From, header.Subject, header.SourceMailboxId, now, decision?.Action);
        var applied = evaluation.AppliedRule is null ? null : rulesById[evaluation.AppliedRule.Id];
        var ruleContext = decision is not null ? "One-message approval" : applied is null ? "No matching reusable rule" : $"{applied.Action} via {applied.MatchType}";
        if (applied?.ExpiresUtc is { } expiry) ruleContext += $"; rule expires {expiry:u}";
        var retention = applied?.DeliveredMessageRetentionDays is { } days
            ? $"Destination retention: {days} days"
            : "Destination retention: forever";
        var deliveryStatus = delivery is null ? evaluation.Outcome switch
        {
            RuleOutcome.Allow when header.BlockedSourceDeletedUtc is not null => "Previously deleted from source by a block rule; not deliverable",
            RuleOutcome.Allow => "Eligible for local delivery",
            RuleOutcome.Block when header.BlockedSourceDeletedUtc is not null => "Deleted from source; not locally delivered",
            RuleOutcome.Block when header.BlockedSourceDeletionError is not null => "Source deletion failed; automatic retry pending",
            RuleOutcome.Block => "Queued for source deletion; not locally delivered",
            _ => "Awaiting review; not locally delivered"
        } :
            delivery.State == MessageDeliveryState.Failed ? $"Failed during {delivery.LastFailureStage}; retry available" : delivery.State.ToString();
        return new(header.Id, header.From ?? "(unknown sender)", header.Subject ?? "(no subject)", source.DisplayName, header.ReceivedUtc,
            $"Source: {source.PollingStatus ?? (source.Enabled ? "enabled" : "disabled")}", evaluation.Outcome, deliveryStatus, ruleContext, retention,
            delivery?.Id, header.SourceMailboxId, header.FolderName, header.Uid, header.UidValidity);
    }
}
