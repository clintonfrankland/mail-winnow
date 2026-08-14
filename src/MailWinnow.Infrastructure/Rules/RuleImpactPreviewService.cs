using MailWinnow.Core.Rules;
using MailWinnow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MailWinnow.Infrastructure.Rules;

public sealed record RuleImpactSample(string Sender, string Subject);
public sealed record RuleImpactPreview(int AllowCount, int BlockCount, IReadOnlyList<RuleImpactSample> Samples)
{
    public int AffectedCount => AllowCount + BlockCount;
    public int OmittedCount => Math.Max(0, AffectedCount - Samples.Count);
}

public interface IRuleImpactPreviewService
{
    Task<RuleImpactPreview> PreviewAsync(string ownerUserId, MailRule proposal, Guid? replacedRuleId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
}

/// <summary>Read-only, owner-bound simulation over catalogued pending headers.</summary>
public sealed class RuleImpactPreviewService(MailWinnowDbContext db) : IRuleImpactPreviewService
{
    public async Task<RuleImpactPreview> PreviewAsync(string ownerUserId, MailRule proposal, Guid? replacedRuleId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        if (proposal.OwnerUserId != ownerUserId) throw new InvalidOperationException("A rule preview is limited to its owner.");
        if (string.IsNullOrWhiteSpace(proposal.MatchValue)) throw new ArgumentException("Rule match value is required.", nameof(proposal));
        if (proposal.Action is not (RuleAction.PermanentlyAllow or RuleAction.TemporarilyAllow or RuleAction.PermanentlyBlock)) throw new ArgumentException("Reusable rules must be allow or block rules.", nameof(proposal));
        if (proposal.Action == RuleAction.TemporarilyAllow && (!proposal.EffectiveUtc.HasValue || !proposal.ExpiresUtc.HasValue || proposal.EffectiveUtc >= proposal.ExpiresUtc)) throw new ArgumentException("Temporary allows require a current effective window.", nameof(proposal));
        if (replacedRuleId is { } id && !await db.MailRules.AnyAsync(x => x.Id == id && x.OwnerUserId == ownerUserId, cancellationToken))
            throw new InvalidOperationException("The rule was not found for this user.");
        if (proposal.Scope == RuleScope.SourceAccount && !await db.SourceMailboxes.AnyAsync(x => x.Id == proposal.SourceMailboxId && x.OwnerUserId == ownerUserId, cancellationToken))
            throw new InvalidOperationException("The source mailbox was not found for this user.");

        var rules = await db.MailRules.AsNoTracking().Where(x => x.OwnerUserId == ownerUserId && x.Id != replacedRuleId).ToListAsync(cancellationToken);
        rules.Add(proposal);
        var candidates = rules.Select(x => new RuleCandidate(x.Id, x.Action, x.Scope, x.MatchType, x.MatchValue, x.SourceMailboxId, x.EffectiveUtc, x.ExpiresUtc)).ToArray();
        var ownedSources = db.SourceMailboxes.Where(x => x.OwnerUserId == ownerUserId).Select(x => x.Id);
        var headers = await db.SourceMessageHeaders.AsNoTracking()
            .Where(x => x.EvaluationOutcome == RuleOutcome.Pending && ownedSources.Contains(x.SourceMailboxId))
            .ToListAsync(cancellationToken);
        var affected = headers.Select(x => new { Header = x, Result = RuleEvaluator.Evaluate(candidates, x.From, x.Subject, x.SourceMailboxId, nowUtc) })
            .Where(x => x.Result.Outcome != RuleOutcome.Pending).ToList();
        var samples = affected.OrderByDescending(x => x.Header.ReceivedUtc).ThenBy(x => x.Header.Id).Take(5)
            .Select(x => new RuleImpactSample(x.Header.From ?? "(unknown sender)", x.Header.Subject ?? "(no subject)")).ToArray();
        return new(affected.Count(x => x.Result.Outcome == RuleOutcome.Allow), affected.Count(x => x.Result.Outcome == RuleOutcome.Block), samples);
    }
}
