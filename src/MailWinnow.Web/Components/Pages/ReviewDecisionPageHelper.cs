using System.Security.Claims;
using MailWinnow.Core.Rules;
using MailWinnow.Infrastructure.Rules;

namespace MailWinnow.Web.Components.Pages;

internal static class ReviewDecisionPageHelper
{
    public static async Task ApplyAsync(ClaimsPrincipal user, ReviewDecisionRequest decision, IRuleManagementService rules, Func<Task> reload, Action<string?> setSaved, Action<string?> setError)
    {
        setError(null);
        try
        {
            var owner = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("The signed-in user could not be identified.");
            if (decision.Action == RuleAction.DeleteOneMessage)
            {
                foreach (var id in decision.MessageIds.Distinct()) await rules.SetMessageDecisionAsync(new MessageDecision { OwnerUserId = owner, SourceMessageHeaderId = id, Action = RuleAction.DeleteOneMessage });
                setSaved(decision.MessageIds.Count == 1 ? "Message queued for deletion." : $"{decision.MessageIds.Count} messages queued for deletion.");
            }
            else
            {
                await rules.AddOrUpdateAsync(new MailRule { OwnerUserId = owner, Action = decision.Action, Scope = RuleScope.User, MatchType = decision.MatchType, MatchValue = decision.MatchValue.Trim(), DeliveredMessageRetentionDays = decision.RetentionDays });
                var verb = decision.Action == RuleAction.PermanentlyBlock ? "Blocked" : "Allowed";
                var target = decision.MatchType == RuleMatchType.ExactSender ? "sender" : "domain";
                setSaved($"{verb} {target} {decision.MatchValue}.");
            }
            await reload();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            setError(ex.Message);
        }
    }
}
