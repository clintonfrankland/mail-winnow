using MailWinnow.Core.Rules;

namespace MailWinnow.Web.Components.Pages;

public sealed record ReviewDecisionRequest(RuleAction Action, RuleMatchType MatchType, string MatchValue, int? RetentionDays, IReadOnlyList<Guid> MessageIds);
