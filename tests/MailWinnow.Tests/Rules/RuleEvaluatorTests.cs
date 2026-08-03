using MailWinnow.Core.Rules;

namespace MailWinnow.Tests.Rules;

public sealed class RuleEvaluatorTests
{
    [Fact]
    public void Resolve_WhenAllowAndBlockMatch_ReturnsBlock()
    {
        var result = RuleEvaluator.Resolve([RuleAction.Allow, RuleAction.Block]);

        Assert.Equal(RuleAction.Block, result);
    }
}
