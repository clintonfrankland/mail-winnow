namespace MailWinnow.Core.Rules;

/// <summary>
/// Provides deterministic resolution for the actions selected by matching rules.
/// </summary>
public static class RuleEvaluator
{
    /// <summary>
    /// Resolves matching actions with safety-first precedence: a matching block wins.
    /// </summary>
    public static RuleAction? Resolve(IEnumerable<RuleAction> matchingActions)
    {
        ArgumentNullException.ThrowIfNull(matchingActions);

        var hasAllow = false;
        foreach (var action in matchingActions)
        {
            if (action == RuleAction.Block)
            {
                return RuleAction.Block;
            }

            hasAllow |= action == RuleAction.Allow;
        }

        return hasAllow ? RuleAction.Allow : null;
    }
}
