using System.Security.Claims;
using MailWinnow.Core.Rules;
using MailWinnow.Infrastructure.Rules;

namespace MailWinnow.Web.Components.Pages;

internal static class ReviewDecisionPageHelper
{
    public static async Task<bool> QueueAsync(ClaimsPrincipal user, ReviewDecisionRequest decision, IReviewDecisionQueue queue, Action<string?> setSaved, Action<string?> setError)
    {
        setError(null);
        setSaved(null);
        try
        {
            var result = await queue.QueueAsync(user, decision.Action, decision.MatchType, decision.MatchValue,
                decision.RetentionDays, decision.MessageIds);
            if (!result.Succeeded) { setError(result.Message); return false; }
            var verb = decision.Action switch { RuleAction.PermanentlyBlock => "Block", RuleAction.DeleteOneMessage => "Deletion", _ => "Allow" };
            setSaved($"{verb} queued.");
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            setError(ex.Message);
            return false;
        }
    }

    public static bool Matches(ReviewDecisionRequest decision, Guid messageId, string sender)
    {
        if (decision.Action == RuleAction.DeleteOneMessage) return decision.MessageIds.Contains(messageId);
        if (decision.MatchType == RuleMatchType.ExactSender)
            return string.Equals(sender, decision.MatchValue, StringComparison.OrdinalIgnoreCase);
        var at = sender.LastIndexOf('@');
        return decision.MatchType == RuleMatchType.SenderDomain && at >= 0 &&
            string.Equals(sender[(at + 1)..], decision.MatchValue.TrimStart('@'), StringComparison.OrdinalIgnoreCase);
    }
}
