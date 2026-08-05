using MailWinnow.Core.Rules;
using MailWinnow.Infrastructure.Mailboxes;
using MailWinnow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MailWinnow.Infrastructure.Rules;

public interface IRuleEvaluationService
{
    Task<RuleEvaluation> EvaluateAsync(string ownerUserId, Guid headerId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
    Task ReevaluatePendingHeadersAsync(string ownerUserId, CancellationToken cancellationToken = default);
}

/// <summary>Owner-bound evaluation and safe pending-header reevaluation on rule changes.</summary>
public sealed class RuleEvaluationService(MailWinnowDbContext db, IMessageDeliveryService? deliveries = null) : IRuleEvaluationService
{
    public async Task<RuleEvaluation> EvaluateAsync(string ownerUserId, Guid headerId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        var header = await HeaderForOwnerAsync(ownerUserId, headerId, cancellationToken);
        var decision = await db.MessageDecisions.SingleOrDefaultAsync(x => x.OwnerUserId == ownerUserId && x.SourceMessageHeaderId == headerId, cancellationToken);
        var rules = await db.MailRules.Where(x => x.OwnerUserId == ownerUserId).ToListAsync(cancellationToken);
        var result = RuleEvaluator.Evaluate(rules.Select(ToCandidate), header.From, header.Subject, header.SourceMailboxId, nowUtc, decision?.Action);
        header.EvaluationOutcome = result.Outcome;
        header.EvaluatedUtc = nowUtc;
        await db.SaveChangesAsync(cancellationToken);
        if (result.Outcome == RuleOutcome.Allow && deliveries is not null)
            await deliveries.QueueApprovedAsync(ownerUserId, headerId, result.AppliedRule?.Id, cancellationToken);
        return result;
    }

    public async Task ReevaluatePendingHeadersAsync(string ownerUserId, CancellationToken cancellationToken = default)
    {
        var headers = await db.SourceMessageHeaders
            .Where(x => x.EvaluationOutcome == RuleOutcome.Pending && db.SourceMailboxes.Any(m => m.Id == x.SourceMailboxId && m.OwnerUserId == ownerUserId))
            .Select(x => x.Id).ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        foreach (var id in headers) await EvaluateAsync(ownerUserId, id, now, cancellationToken);
    }

    private async Task<SourceMessageHeader> HeaderForOwnerAsync(string ownerUserId, Guid headerId, CancellationToken cancellationToken) =>
        await db.SourceMessageHeaders.SingleOrDefaultAsync(x => x.Id == headerId && db.SourceMailboxes.Any(m => m.Id == x.SourceMailboxId && m.OwnerUserId == ownerUserId), cancellationToken)
        ?? throw new InvalidOperationException("The message header was not found for this user.");

    private static RuleCandidate ToCandidate(MailRule rule) => new(rule.Id, rule.Action, rule.Scope, rule.MatchType, rule.MatchValue, rule.SourceMailboxId, rule.EffectiveUtc, rule.ExpiresUtc);
}

public interface IRuleManagementService
{
    Task AddOrUpdateAsync(MailRule rule, CancellationToken cancellationToken = default);
    Task ReplaceAsync(string ownerUserId, Guid ruleId, MailRule replacement, CancellationToken cancellationToken = default);
    Task DeleteAsync(string ownerUserId, Guid ruleId, CancellationToken cancellationToken = default);
    Task SetMessageDecisionAsync(MessageDecision decision, CancellationToken cancellationToken = default);
    Task DeleteMessageDecisionAsync(string ownerUserId, Guid headerId, CancellationToken cancellationToken = default);
}

/// <summary>Writes rules only after validating their scope, temporary dates, and ownership.</summary>
public sealed class RuleManagementService(MailWinnowDbContext db, IRuleEvaluationService evaluation) : IRuleManagementService
{
    public async Task AddOrUpdateAsync(MailRule rule, CancellationToken cancellationToken = default)
    {
        Validate(rule);
        if (rule.Scope == RuleScope.SourceAccount && !await db.SourceMailboxes.AnyAsync(x => x.Id == rule.SourceMailboxId && x.OwnerUserId == rule.OwnerUserId, cancellationToken))
            throw new InvalidOperationException("The source mailbox was not found for this user.");
        var existing = await db.MailRules.SingleOrDefaultAsync(x => x.Id == rule.Id && x.OwnerUserId == rule.OwnerUserId, cancellationToken);
        if (existing is null) db.MailRules.Add(rule); else db.Entry(existing).CurrentValues.SetValues(rule);
        await db.SaveChangesAsync(cancellationToken);
        await ReevaluateOwnedHeadersAsync(rule.OwnerUserId, cancellationToken);
    }

    public async Task DeleteAsync(string ownerUserId, Guid ruleId, CancellationToken cancellationToken = default)
    {
        var rule = await db.MailRules.SingleOrDefaultAsync(x => x.Id == ruleId && x.OwnerUserId == ownerUserId, cancellationToken)
            ?? throw new InvalidOperationException("The rule was not found for this user.");
        db.MailRules.Remove(rule);
        await db.SaveChangesAsync(cancellationToken);
        await ReevaluateOwnedHeadersAsync(ownerUserId, cancellationToken);
    }

    public async Task ReplaceAsync(string ownerUserId, Guid ruleId, MailRule replacement, CancellationToken cancellationToken = default)
    {
        if (replacement.OwnerUserId != ownerUserId)
            throw new InvalidOperationException("A rule can only be replaced by its owner.");
        Validate(replacement);
        if (replacement.Scope == RuleScope.SourceAccount && !await db.SourceMailboxes.AnyAsync(x => x.Id == replacement.SourceMailboxId && x.OwnerUserId == ownerUserId, cancellationToken))
            throw new InvalidOperationException("The source mailbox was not found for this user.");

        var existing = await db.MailRules.SingleOrDefaultAsync(x => x.Id == ruleId && x.OwnerUserId == ownerUserId, cancellationToken)
            ?? throw new InvalidOperationException("The rule was not found for this user.");
        db.MailRules.Remove(existing);
        db.MailRules.Add(replacement);
        await db.SaveChangesAsync(cancellationToken);
        await ReevaluateOwnedHeadersAsync(ownerUserId, cancellationToken);
    }

    public async Task SetMessageDecisionAsync(MessageDecision decision, CancellationToken cancellationToken = default)
    {
        if (decision.Action is not (RuleAction.ApproveOneMessage or RuleAction.PendingReview)) throw new ArgumentException("Message decisions must be approve-one-message or pending-review.", nameof(decision));
        var headerExists = await db.SourceMessageHeaders.AnyAsync(x => x.Id == decision.SourceMessageHeaderId && db.SourceMailboxes.Any(m => m.Id == x.SourceMailboxId && m.OwnerUserId == decision.OwnerUserId), cancellationToken);
        if (!headerExists) throw new InvalidOperationException("The message header was not found for this user.");
        var existing = await db.MessageDecisions.SingleOrDefaultAsync(x => x.OwnerUserId == decision.OwnerUserId && x.SourceMessageHeaderId == decision.SourceMessageHeaderId, cancellationToken);
        if (existing is null) db.MessageDecisions.Add(decision); else db.Entry(existing).CurrentValues.SetValues(decision);
        await db.SaveChangesAsync(cancellationToken);
        await evaluation.EvaluateAsync(decision.OwnerUserId, decision.SourceMessageHeaderId, DateTimeOffset.UtcNow, cancellationToken);
    }

    public async Task DeleteMessageDecisionAsync(string ownerUserId, Guid headerId, CancellationToken cancellationToken = default)
    {
        var decision = await db.MessageDecisions.SingleOrDefaultAsync(x => x.OwnerUserId == ownerUserId && x.SourceMessageHeaderId == headerId, cancellationToken)
            ?? throw new InvalidOperationException("The message decision was not found for this user.");
        db.MessageDecisions.Remove(decision);
        await db.SaveChangesAsync(cancellationToken);
        await evaluation.EvaluateAsync(ownerUserId, headerId, DateTimeOffset.UtcNow, cancellationToken);
    }

    private async Task ReevaluateOwnedHeadersAsync(string ownerUserId, CancellationToken cancellationToken)
    {
        var ids = await db.SourceMessageHeaders.Where(x => db.SourceMailboxes.Any(m => m.Id == x.SourceMailboxId && m.OwnerUserId == ownerUserId)).Select(x => x.Id).ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        foreach (var id in ids) await evaluation.EvaluateAsync(ownerUserId, id, now, cancellationToken);
    }

    private static void Validate(MailRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.OwnerUserId) || string.IsNullOrWhiteSpace(rule.MatchValue)) throw new ArgumentException("Rule owner and match value are required.", nameof(rule));
        if (rule.Action is not (RuleAction.PermanentlyAllow or RuleAction.TemporarilyAllow or RuleAction.PermanentlyBlock))
            throw new ArgumentException("Reusable rules must be permanent allow, temporary allow, or permanent block.", nameof(rule));
        if (rule.Scope == RuleScope.SourceAccount && rule.SourceMailboxId is null) throw new ArgumentException("Account-scoped rules require a source mailbox.", nameof(rule));
        if (rule.Scope == RuleScope.User && rule.SourceMailboxId is not null) throw new ArgumentException("User-scoped rules cannot target a source mailbox.", nameof(rule));
        if (rule.Action == RuleAction.TemporarilyAllow && (!rule.EffectiveUtc.HasValue || !rule.ExpiresUtc.HasValue || rule.EffectiveUtc >= rule.ExpiresUtc)) throw new ArgumentException("Temporary allows require an effective time before their expiration.", nameof(rule));
        if (rule.Action != RuleAction.TemporarilyAllow && (rule.EffectiveUtc.HasValue || rule.ExpiresUtc.HasValue)) throw new ArgumentException("Effective and expiration dates are only valid for temporary allows.", nameof(rule));
        if (rule.DeliveredMessageRetentionDays is < 0) throw new ArgumentException("Retention days cannot be negative.", nameof(rule));
    }
}
