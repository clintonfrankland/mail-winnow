using MailWinnow.Core.Rules;
using MailWinnow.Web.Components.Pages;

namespace MailWinnow.Tests.Review;

public sealed class ReviewDecisionPageHelperTests
{
    [Fact]
    public void Matches_UsesParsedAddressForExactSenderAndDomainRules()
    {
        var exact = new ReviewDecisionRequest(RuleAction.PermanentlyAllow, RuleMatchType.ExactSender, "sender@example.test", null, []);
        var domain = new ReviewDecisionRequest(RuleAction.PermanentlyBlock, RuleMatchType.SenderDomain, "example.test", null, []);

        Assert.True(ReviewDecisionPageHelper.Matches(exact, Guid.Empty, "\"Example Sender\" <SENDER@example.test>"));
        Assert.True(ReviewDecisionPageHelper.Matches(domain, Guid.Empty, "Example Sender <sender@EXAMPLE.test>"));
        Assert.False(ReviewDecisionPageHelper.Matches(exact, Guid.Empty, "first@example.test, sender@example.test"));
    }
}
