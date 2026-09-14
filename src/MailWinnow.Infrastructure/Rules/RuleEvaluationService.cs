using MailWinnow.Core.Rules;
using MailWinnow.Infrastructure.Mailboxes;
using MailWinnow.Infrastructure.Persistence;
using MailWinnow.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace MailWinnow.Infrastructure.Rules;

public interface IRuleEvaluationService
{
    Task<RuleEvaluation> EvaluateAsync(string ownerUserId, Guid headerId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
    Task ReevaluatePendingHeadersAsync(string ownerUserId, CancellationToken cancellationToken = default);
    Task ReevaluateOwnedHeadersAsync(string ownerUserId, CancellationToken cancellationToken = default);
}

/// <summary>Owner-bound evaluation and safe pending-header reevaluation on rule changes.</summary>
public sealed class RuleEvaluationService(MailWinnowDbContext db, IMessageDeliveryService? deliveries = null, IAuditRecorder? audit = null) : IRuleEvaluationService
{
    public async Task<RuleEvaluation> EvaluateAsync(string ownerUserId, Guid headerId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        var header = await HeaderForOwnerAsync(ownerUserId, headerId, cancellationToken);
        var decision = await db.MessageDecisions.SingleOrDefaultAsync(x => x.OwnerUserId == ownerUserId && x.SourceMessageHeaderId == headerId, cancellationToken);
        var rules = await db.MailRules.AsNoTracking().Where(x => x.OwnerUserId == ownerUserId).ToListAsync(cancellationToken);
        var result = RuleEvaluator.Evaluate(rules.Select(ToCandidate), header.From, header.Subject, header.SourceMailboxId, nowUtc, decision?.Action);
        header.EvaluationOutcome = result.Outcome;
        header.EvaluatedUtc = nowUtc;
        await db.SaveChangesAsync(cancellationToken);
        if (audit is not null) await audit.RecordAsync("decision.evaluated", ownerUserId, ownerUserId, "header", headerId.ToString("N"), cancellationToken: cancellationToken);
        if (result.Outcome == RuleOutcome.Allow && deliveries is not null)
            await deliveries.QueueApprovedAsync(ownerUserId, headerId, result.AppliedRule?.Id, cancellationToken);
        return result;
    }

    public async Task ReevaluatePendingHeadersAsync(string ownerUserId, CancellationToken cancellationToken = default)
    {
        await ReevaluateAsync(ownerUserId, true, cancellationToken);
    }

    public Task ReevaluateOwnedHeadersAsync(string ownerUserId, CancellationToken cancellationToken = default) => ReevaluateAsync(ownerUserId, false, cancellationToken);

    private async Task ReevaluateAsync(string ownerUserId, bool pendingOnly, CancellationToken cancellationToken)
    {
        var rules = await db.MailRules.AsNoTracking().Where(x => x.OwnerUserId == ownerUserId).ToListAsync(cancellationToken);
        var candidates = RuleEvaluator.Compile(rules.Select(ToCandidate));
        var now = DateTimeOffset.UtcNow;
        Guid? cursor = null;
        while (true)
        {
            var query = db.SourceMessageHeaders.Where(x => db.SourceMailboxes.Any(m => m.Id == x.SourceMailboxId && m.OwnerUserId == ownerUserId));
            if (pendingOnly) query = query.Where(x => x.EvaluationOutcome == RuleOutcome.Pending);
            if (cursor is { } after) query = query.Where(x => x.Id.CompareTo(after) > 0);
            var headers = await query.OrderBy(x => x.Id).Take(200).ToListAsync(cancellationToken);
            if (headers.Count == 0) return;
            var ids = headers.Select(x => x.Id).ToArray();
            var decisions = await db.MessageDecisions.AsNoTracking().Where(x => x.OwnerUserId == ownerUserId && ids.Contains(x.SourceMessageHeaderId)).ToDictionaryAsync(x => x.SourceMessageHeaderId, cancellationToken);
            var approved = new List<(Guid HeaderId, Guid? RuleId)>();
            foreach (var header in headers)
            {
                var result = candidates.Evaluate(header.From, header.Subject, header.SourceMailboxId, now, decisions.GetValueOrDefault(header.Id)?.Action);
                if (header.EvaluationOutcome != result.Outcome || header.EvaluatedUtc is null)
                {
                    header.EvaluationOutcome = result.Outcome;
                    header.EvaluatedUtc = now;
                }
                if (result.Outcome == RuleOutcome.Allow && header.BlockedSourceDeletedUtc is null)
                    approved.Add((header.Id, result.AppliedRule?.Id));
            }
            await db.SaveChangesAsync(cancellationToken);
            if (deliveries is not null && approved.Count > 0)
            {
                var existingDeliveryIds = (await db.MessageDeliveries.AsNoTracking()
                    .Where(delivery => delivery.OwnerUserId == ownerUserId && ids.Contains(delivery.SourceMessageHeaderId))
                    .Select(delivery => delivery.SourceMessageHeaderId).ToListAsync(cancellationToken)).ToHashSet();
                foreach (var approvedHeader in approved.Where(header => !existingDeliveryIds.Contains(header.HeaderId)))
                    await deliveries.QueueApprovedAsync(ownerUserId, approvedHeader.HeaderId, approvedHeader.RuleId, cancellationToken);
            }
            cursor = headers[^1].Id;
            // Detach only this page's saved data. Never Clear a shared context: the caller
            // may own the durable work item whose completion still needs to be committed.
            foreach (var header in headers) db.Entry(header).State = EntityState.Detached;
            foreach (var entry in db.ChangeTracker.Entries<MessageDelivery>()
                .Where(entry => ids.Contains(entry.Entity.SourceMessageHeaderId) && entry.State == EntityState.Unchanged).ToArray())
                entry.State = EntityState.Detached;
        }
    }

    private async Task<SourceMessageHeader> HeaderForOwnerAsync(string ownerUserId, Guid headerId, CancellationToken cancellationToken) =>
        await db.SourceMessageHeaders.SingleOrDefaultAsync(x => x.Id == headerId && db.SourceMailboxes.Any(m => m.Id == x.SourceMailboxId && m.OwnerUserId == ownerUserId), cancellationToken)
        ?? throw new InvalidOperationException("The message header was not found for this user.");

    private static RuleCandidate ToCandidate(MailRule rule) => new(rule.Id, rule.Action, rule.Scope, rule.MatchType, rule.MatchValue, rule.SourceMailboxId, rule.EffectiveUtc, rule.ExpiresUtc);
}

public interface IRuleManagementService
{
    Task AddOrUpdateAsync(MailRule rule, CancellationToken cancellationToken = default);
    Task PersistAsync(MailRule rule, CancellationToken cancellationToken = default);
    Task ReplaceAsync(string ownerUserId, Guid ruleId, MailRule replacement, CancellationToken cancellationToken = default);
    Task DeleteAsync(string ownerUserId, Guid ruleId, CancellationToken cancellationToken = default);
    Task SetMessageDecisionAsync(MessageDecision decision, CancellationToken cancellationToken = default);
    Task DeleteMessageDecisionAsync(string ownerUserId, Guid headerId, CancellationToken cancellationToken = default);
}

/// <summary>Writes rules only after validating their scope, temporary dates, and ownership.</summary>
public sealed class RuleManagementService(MailWinnowDbContext db, IRuleEvaluationService evaluation, IAuditRecorder? audit = null) : IRuleManagementService
{
    public async Task AddOrUpdateAsync(MailRule rule, CancellationToken cancellationToken = default)
    {
        if (await PersistRuleAsync(rule, cancellationToken))
            await ReevaluateOwnedHeadersAsync(rule.OwnerUserId, cancellationToken);
    }

    /// <summary>The durable queue owns evaluation completion and must replay it even for an identical saved rule.</summary>
    public async Task PersistAsync(MailRule rule, CancellationToken cancellationToken = default) =>
        await PersistRuleAsync(rule, cancellationToken);

    private async Task<bool> PersistRuleAsync(MailRule rule, CancellationToken cancellationToken)
    {
        Validate(rule);
        if (rule.Scope == RuleScope.SourceAccount && !await db.SourceMailboxes.AnyAsync(x => x.Id == rule.SourceMailboxId && x.OwnerUserId == rule.OwnerUserId, cancellationToken))
            throw new InvalidOperationException("The source mailbox was not found for this user.");
        var existing = await db.MailRules.SingleOrDefaultAsync(x => x.Id == rule.Id && x.OwnerUserId == rule.OwnerUserId, cancellationToken);
        if (existing is not null && HasSameConfiguration(existing, rule)) return false;
        if (existing is null) db.MailRules.Add(rule); else db.Entry(existing).CurrentValues.SetValues(rule);
        await db.SaveChangesAsync(cancellationToken);
        if (audit is not null) await audit.RecordAsync(existing is null ? "rule.created" : "rule.updated", rule.OwnerUserId, rule.OwnerUserId, "rule", rule.Id.ToString("N"), cancellationToken: cancellationToken);
        return true;
    }

    public async Task DeleteAsync(string ownerUserId, Guid ruleId, CancellationToken cancellationToken = default)
    {
        var rule = await db.MailRules.SingleOrDefaultAsync(x => x.Id == ruleId && x.OwnerUserId == ownerUserId, cancellationToken)
            ?? throw new InvalidOperationException("The rule was not found for this user.");
        db.MailRules.Remove(rule);
        await db.SaveChangesAsync(cancellationToken);
        if (audit is not null) await audit.RecordAsync("rule.deleted", ownerUserId, ownerUserId, "rule", ruleId.ToString("N"), cancellationToken: cancellationToken);
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
        if (audit is not null) await audit.RecordAsync("rule.replaced", ownerUserId, ownerUserId, "rule", replacement.Id.ToString("N"), cancellationToken: cancellationToken);
        await ReevaluateOwnedHeadersAsync(ownerUserId, cancellationToken);
    }

    public async Task SetMessageDecisionAsync(MessageDecision decision, CancellationToken cancellationToken = default)
    {
        if (decision.Action is not (RuleAction.ApproveOneMessage or RuleAction.PendingReview or RuleAction.DeleteOneMessage)) throw new ArgumentException("Message decisions must be approve-one-message, delete-one-message, or pending-review.", nameof(decision));
        var headerExists = await db.SourceMessageHeaders.AnyAsync(x => x.Id == decision.SourceMessageHeaderId && db.SourceMailboxes.Any(m => m.Id == x.SourceMailboxId && m.OwnerUserId == decision.OwnerUserId), cancellationToken);
        if (!headerExists) throw new InvalidOperationException("The message header was not found for this user.");
        var existing = await db.MessageDecisions.SingleOrDefaultAsync(x => x.OwnerUserId == decision.OwnerUserId && x.SourceMessageHeaderId == decision.SourceMessageHeaderId, cancellationToken);
        if (existing is null) db.MessageDecisions.Add(decision); else db.Entry(existing).CurrentValues.SetValues(decision);
        await db.SaveChangesAsync(cancellationToken);
        if (audit is not null) await audit.RecordAsync("decision.created", decision.OwnerUserId, decision.OwnerUserId, "header", decision.SourceMessageHeaderId.ToString("N"), cancellationToken: cancellationToken);
        await evaluation.EvaluateAsync(decision.OwnerUserId, decision.SourceMessageHeaderId, DateTimeOffset.UtcNow, cancellationToken);
    }

    public async Task DeleteMessageDecisionAsync(string ownerUserId, Guid headerId, CancellationToken cancellationToken = default)
    {
        var decision = await db.MessageDecisions.SingleOrDefaultAsync(x => x.OwnerUserId == ownerUserId && x.SourceMessageHeaderId == headerId, cancellationToken)
            ?? throw new InvalidOperationException("The message decision was not found for this user.");
        db.MessageDecisions.Remove(decision);
        await db.SaveChangesAsync(cancellationToken);
        if (audit is not null) await audit.RecordAsync("decision.deleted", ownerUserId, ownerUserId, "header", headerId.ToString("N"), cancellationToken: cancellationToken);
        await evaluation.EvaluateAsync(ownerUserId, headerId, DateTimeOffset.UtcNow, cancellationToken);
    }

    private async Task ReevaluateOwnedHeadersAsync(string ownerUserId, CancellationToken cancellationToken)
    {
        await evaluation.ReevaluateOwnedHeadersAsync(ownerUserId, cancellationToken);
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
        if (rule.DeliveredMessageRetentionDays is { } days && days is not (30 or 7 or 3 or 1))
            throw new ArgumentException("Destination retention must be Forever, 1 month, 1 week, 3 days, or 1 day.", nameof(rule));
    }

    private static bool HasSameConfiguration(MailRule existing, MailRule proposed) =>
        existing.OwnerUserId == proposed.OwnerUserId && existing.Action == proposed.Action && existing.Scope == proposed.Scope &&
        existing.MatchType == proposed.MatchType && existing.MatchValue == proposed.MatchValue &&
        existing.SourceMailboxId == proposed.SourceMailboxId && existing.EffectiveUtc == proposed.EffectiveUtc &&
        existing.ExpiresUtc == proposed.ExpiresUtc && existing.DeliveredMessageRetentionDays == proposed.DeliveredMessageRetentionDays;
}
