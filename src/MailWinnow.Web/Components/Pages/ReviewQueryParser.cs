using MailWinnow.Core.Rules;

namespace MailWinnow.Web.Components.Pages;

internal static class ReviewQueryParser
{
    public static RuleOutcome? ParseOutcome(string? value) =>
        Enum.TryParse<RuleOutcome>(value, ignoreCase: true, out var outcome)
        && Enum.IsDefined(outcome)
            ? outcome
            : null;
}
