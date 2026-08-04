using System.Text;

namespace MailWinnow.Core.Rules;

public enum RuleMatchType { ExactSender, SenderDomain, NormalizedExactSubject, SubjectContains }
public enum RuleScope { User, SourceAccount }
public enum RuleOutcome { Allow, Block, Pending }

public sealed record RuleCandidate(
    Guid Id,
    RuleAction Action,
    RuleScope Scope,
    RuleMatchType MatchType,
    string MatchValue,
    Guid? SourceMailboxId,
    DateTimeOffset? EffectiveUtc = null,
    DateTimeOffset? ExpiresUtc = null);

public sealed record RuleEvaluation(
    RuleOutcome Outcome,
    RuleCandidate? AppliedRule,
    IReadOnlyList<RuleCandidate> ConflictingRules)
{
    public bool HasConflict => ConflictingRules.Count > 0;
}

/// <summary>Pure, deterministic evaluation for a single catalogued message.</summary>
public static class RuleEvaluator
{
    public static RuleEvaluation Evaluate(
        IEnumerable<RuleCandidate> rules,
        string? sender,
        string? subject,
        Guid sourceMailboxId,
        DateTimeOffset nowUtc,
        RuleAction? explicitMessageDecision = null)
    {
        ArgumentNullException.ThrowIfNull(rules);

        if (explicitMessageDecision is not null)
        {
            return new RuleEvaluation(OutcomeFor(explicitMessageDecision.Value), null, []);
        }

        var matched = rules.Where(rule => IsActive(rule, nowUtc) && Matches(rule, sender, subject, sourceMailboxId)).ToList();
        foreach (var rank in new[]
                 {
                     (RuleScope.SourceAccount, RuleOutcome.Block),
                     (RuleScope.SourceAccount, RuleOutcome.Allow),
                     (RuleScope.User, RuleOutcome.Block),
                     (RuleScope.User, RuleOutcome.Allow)
                 })
        {
            var candidates = matched.Where(x => x.Scope == rank.Item1 && OutcomeFor(x.Action) == rank.Item2).OrderBy(x => x.Id).ToList();
            if (candidates.Count != 0)
            {
                // Keep every opposing match so callers can explain precedence instead of hiding a surprise.
                var opposite = matched.Where(x => OutcomeFor(x.Action) != rank.Item2).OrderBy(x => x.Id).ToList();
                return new RuleEvaluation(rank.Item2, candidates[0], opposite);
            }
        }

        return new RuleEvaluation(RuleOutcome.Pending, null, []);
    }

    public static string NormalizeSubject(string? subject) => string.Join(' ', (subject ?? string.Empty).Normalize(NormalizationForm.FormKC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    private static bool IsActive(RuleCandidate rule, DateTimeOffset nowUtc) =>
        rule.Action != RuleAction.TemporarilyAllow ||
        ((!rule.EffectiveUtc.HasValue || rule.EffectiveUtc <= nowUtc) && (!rule.ExpiresUtc.HasValue || rule.ExpiresUtc > nowUtc));

    private static bool Matches(RuleCandidate rule, string? sender, string? subject, Guid sourceMailboxId)
    {
        if (rule.Scope == RuleScope.SourceAccount && rule.SourceMailboxId != sourceMailboxId) return false;
        var value = rule.MatchValue.Trim();
        return rule.MatchType switch
        {
            RuleMatchType.ExactSender => string.Equals(sender?.Trim(), value, StringComparison.OrdinalIgnoreCase),
            RuleMatchType.SenderDomain => string.Equals(SenderDomain(sender), value.TrimStart('@'), StringComparison.OrdinalIgnoreCase),
            RuleMatchType.NormalizedExactSubject => string.Equals(NormalizeSubject(subject), NormalizeSubject(value), StringComparison.Ordinal),
            RuleMatchType.SubjectContains => NormalizeSubject(subject).Contains(NormalizeSubject(value), StringComparison.Ordinal),
            _ => false
        };
    }

    private static string? SenderDomain(string? sender)
    {
        var address = sender?.Trim();
        var at = address?.LastIndexOf('@') ?? -1;
        return at >= 0 && at < address!.Length - 1 ? address[(at + 1)..] : null;
    }

    private static RuleOutcome OutcomeFor(RuleAction action) => action switch
    {
        RuleAction.PermanentlyBlock => RuleOutcome.Block,
        RuleAction.PermanentlyAllow or RuleAction.TemporarilyAllow or RuleAction.ApproveOneMessage => RuleOutcome.Allow,
        _ => RuleOutcome.Pending
    };
}
