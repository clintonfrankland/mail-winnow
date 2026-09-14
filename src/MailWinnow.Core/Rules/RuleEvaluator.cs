using System.Text;
using System.Net.Mail;

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

    /// <summary>Index reusable match keys once per catalogue pass, not once per message/rule pair.</summary>
    public static CompiledRuleSet Compile(IEnumerable<RuleCandidate> rules) => new(rules);

    public sealed class CompiledRuleSet
    {
        private readonly Dictionary<string, RuleCandidate[]> senderRules;
        private readonly Dictionary<string, RuleCandidate[]> domainRules;
        private readonly Dictionary<string, RuleCandidate[]> subjectRules;
        private readonly RuleCandidate[] containsRules;
        private readonly RuleCandidate[] unnormalizedDomainRules;

        internal CompiledRuleSet(IEnumerable<RuleCandidate> rules)
        {
            ArgumentNullException.ThrowIfNull(rules);
            var snapshot = rules.ToArray();
            senderRules = Index(snapshot, RuleMatchType.ExactSender, NormalizeSenderAddress);
            domainRules = Index(snapshot, RuleMatchType.SenderDomain, NormalizeRuleDomain);
            subjectRules = Index(snapshot, RuleMatchType.NormalizedExactSubject, NormalizeSubject);
            containsRules = snapshot.Where(rule => rule.MatchType == RuleMatchType.SubjectContains).ToArray();
            // Preserve canonical behavior even for legacy malformed domain rules.
            unnormalizedDomainRules = snapshot.Where(rule => rule.MatchType == RuleMatchType.SenderDomain &&
                NormalizeRuleDomain(rule.MatchValue) is null).ToArray();
        }

        public RuleEvaluation Evaluate(string? sender, string? subject, Guid sourceMailboxId,
            DateTimeOffset nowUtc, RuleAction? explicitMessageDecision = null)
        {
            if (explicitMessageDecision is not null)
                return RuleEvaluator.Evaluate([], sender, subject, sourceMailboxId, nowUtc, explicitMessageDecision);
            var address = NormalizeSenderAddress(sender);
            var at = address?.LastIndexOf('@') ?? -1;
            var domain = at < 0 ? null : address![(at + 1)..];
            var candidates = Lookup(senderRules, address)
                .Concat(Lookup(domainRules, domain))
                .Concat(domain is null ? unnormalizedDomainRules : [])
                .Concat(Lookup(subjectRules, NormalizeSubject(subject)))
                .Concat(containsRules);
            // The canonical evaluator still owns time windows, account scope, precedence,
            // explicit decisions, deterministic tie-breaking, and conflict explanations.
            return RuleEvaluator.Evaluate(candidates, sender, subject, sourceMailboxId, nowUtc);
        }

        private static Dictionary<string, RuleCandidate[]> Index(IEnumerable<RuleCandidate> rules,
            RuleMatchType matchType, Func<string?, string?> normalize) => rules
            .Where(rule => rule.MatchType == matchType)
            .Select(rule => (Rule: rule, Key: normalize(rule.MatchValue)))
            .Where(item => item.Key is not null)
            .GroupBy(item => item.Key!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Rule).ToArray(), StringComparer.OrdinalIgnoreCase);

        private static IEnumerable<RuleCandidate> Lookup(Dictionary<string, RuleCandidate[]> rules, string? key) =>
            key is not null && rules.TryGetValue(key, out var matches) ? matches : [];
    }

    public static string NormalizeSubject(string? subject) => string.Join(' ', (subject ?? string.Empty).Normalize(NormalizationForm.FormKC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    /// <summary>Returns the address from a valid, single-mailbox From header, or null when it is unavailable or ambiguous.</summary>
    public static string? NormalizeSenderAddress(string? sender)
    {
        if (string.IsNullOrWhiteSpace(sender)) return null;
        var value = sender.Trim();
        if (HasTopLevelMailboxSeparator(value)) return null;

        try
        {
            var address = new MailAddress(value).Address;
            if (string.IsNullOrWhiteSpace(address)) return null;

            address = address.Trim();
            var at = address.LastIndexOf('@');
            return at > 0 && at < address.Length - 1 ? address : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static bool SenderAddressMatches(string? sender, string? matchValue)
    {
        var senderAddress = NormalizeSenderAddress(sender);
        return senderAddress is not null &&
            string.Equals(senderAddress, NormalizeSenderAddress(matchValue), StringComparison.OrdinalIgnoreCase);
    }

    public static bool SenderDomainMatches(string? sender, string? matchValue) =>
        !string.IsNullOrWhiteSpace(matchValue) &&
        string.Equals(SenderDomain(sender), NormalizeRuleDomain(matchValue), StringComparison.OrdinalIgnoreCase);

    private static bool IsActive(RuleCandidate rule, DateTimeOffset nowUtc) =>
        rule.Action != RuleAction.TemporarilyAllow ||
        ((!rule.EffectiveUtc.HasValue || rule.EffectiveUtc <= nowUtc) && (!rule.ExpiresUtc.HasValue || rule.ExpiresUtc > nowUtc));

    private static bool Matches(RuleCandidate rule, string? sender, string? subject, Guid sourceMailboxId)
    {
        if (rule.Scope == RuleScope.SourceAccount && rule.SourceMailboxId != sourceMailboxId) return false;
        var value = rule.MatchValue.Trim();
        return rule.MatchType switch
        {
            RuleMatchType.ExactSender => SenderAddressMatches(sender, value),
            RuleMatchType.SenderDomain => SenderDomainMatches(sender, value),
            RuleMatchType.NormalizedExactSubject => string.Equals(NormalizeSubject(subject), NormalizeSubject(value), StringComparison.Ordinal),
            RuleMatchType.SubjectContains => NormalizeSubject(subject).Contains(NormalizeSubject(value), StringComparison.Ordinal),
            _ => false
        };
    }

    private static string? SenderDomain(string? sender)
    {
        var address = NormalizeSenderAddress(sender);
        var at = address?.LastIndexOf('@') ?? -1;
        return at >= 0 && at < address!.Length - 1 ? address[(at + 1)..] : null;
    }

    private static string? NormalizeRuleDomain(string? matchValue)
    {
        if (string.IsNullOrWhiteSpace(matchValue)) return null;
        var address = NormalizeSenderAddress(matchValue);
        if (address is not null) return SenderDomain(address);

        // Before sender normalization, the review UI derived domains from the raw
        // From header and persisted the closing angle bracket (for example,
        // "example.test>"). Preserve those existing rules while new rules store
        // the canonical bare domain.
        var domain = matchValue.Trim().TrimStart('@').TrimEnd('>').Trim();
        return string.IsNullOrWhiteSpace(domain) ? null : domain;
    }

    private static bool HasTopLevelMailboxSeparator(string value)
    {
        var inQuotes = false;
        var escaped = false;
        var angleDepth = 0;
        foreach (var character in value)
        {
            if (escaped) { escaped = false; continue; }
            if (inQuotes && character == '\\') { escaped = true; continue; }
            if (character == '"') { inQuotes = !inQuotes; continue; }
            if (inQuotes) continue;
            if (character == '<') { angleDepth++; continue; }
            if (character == '>') { if (angleDepth > 0) angleDepth--; continue; }
            if (angleDepth == 0 && character is ',' or ';' or ':') return true;
        }

        return false;
    }

    private static RuleOutcome OutcomeFor(RuleAction action) => action switch
    {
        RuleAction.PermanentlyBlock or RuleAction.DeleteOneMessage => RuleOutcome.Block,
        RuleAction.PermanentlyAllow or RuleAction.TemporarilyAllow or RuleAction.ApproveOneMessage => RuleOutcome.Allow,
        _ => RuleOutcome.Pending
    };
}
