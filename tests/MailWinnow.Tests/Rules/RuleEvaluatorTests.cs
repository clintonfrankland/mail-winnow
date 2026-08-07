using MailWinnow.Core.Rules;

namespace MailWinnow.Tests.Rules;

public sealed class RuleEvaluatorTests
{
    private static readonly Guid AccountId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Evaluate_ExplicitMessageDecision_WinsOverEveryRule()
    {
        var result = Evaluate([Rule(RuleAction.PermanentlyBlock, RuleScope.SourceAccount)] , RuleAction.ApproveOneMessage);

        Assert.Equal(RuleOutcome.Allow, result.Outcome);
        Assert.Null(result.AppliedRule);
    }

    [Fact]
    public void Evaluate_DeleteMessageDecision_BlocksOnlyThatMessage()
    {
        var result = Evaluate([Rule(RuleAction.PermanentlyAllow, RuleScope.User)], RuleAction.DeleteOneMessage);

        Assert.Equal(RuleOutcome.Block, result.Outcome);
        Assert.Null(result.AppliedRule);
    }

    [Fact]
    public void Evaluate_UsesAccountBlockBeforeAccountAllowAndUserRules()
    {
        var result = Evaluate([
            Rule(RuleAction.PermanentlyAllow, RuleScope.User),
            Rule(RuleAction.PermanentlyAllow, RuleScope.SourceAccount),
            Rule(RuleAction.PermanentlyBlock, RuleScope.SourceAccount)]);

        Assert.Equal(RuleOutcome.Block, result.Outcome);
        Assert.Equal(RuleScope.SourceAccount, result.AppliedRule!.Scope);
        Assert.Equal(RuleAction.PermanentlyBlock, result.AppliedRule.Action);
        Assert.True(result.HasConflict);
    }

    [Fact]
    public void Evaluate_UsesUserBlockBeforeUserAllow()
    {
        var result = Evaluate([Rule(RuleAction.PermanentlyAllow, RuleScope.User), Rule(RuleAction.PermanentlyBlock, RuleScope.User)]);

        Assert.Equal(RuleOutcome.Block, result.Outcome);
    }

    [Fact]
    public void Evaluate_ReturnsPendingWhenNoRuleMatches()
    {
        var result = RuleEvaluator.Evaluate([Rule(RuleAction.PermanentlyAllow, RuleScope.User, "other@example.test")], "sender@example.test", "Invoice", AccountId, Now);

        Assert.Equal(RuleOutcome.Pending, result.Outcome);
    }

    [Fact]
    public void Evaluate_RespectsTemporaryEffectiveAndExpirationDates()
    {
        var temporary = Rule(RuleAction.TemporarilyAllow, RuleScope.User) with { EffectiveUtc = Now.AddHours(-1), ExpiresUtc = Now.AddHours(1) };

        Assert.Equal(RuleOutcome.Allow, Evaluate([temporary]).Outcome);
        Assert.Equal(RuleOutcome.Pending, RuleEvaluator.Evaluate([temporary], "sender@example.test", "Invoice", AccountId, Now.AddHours(1)).Outcome);
    }

    [Theory]
    [InlineData(RuleMatchType.ExactSender, "sender@example.test", "sender@example.test", "anything")]
    [InlineData(RuleMatchType.SenderDomain, "example.test", "sender@example.test", "anything")]
    [InlineData(RuleMatchType.NormalizedExactSubject, "Invoice   August", "sender@example.test", " invoice august ")]
    [InlineData(RuleMatchType.SubjectContains, "August", "sender@example.test", "Invoice for august")]
    public void Evaluate_SupportsEveryMatchType(RuleMatchType type, string value, string sender, string subject)
    {
        var rule = Rule(RuleAction.PermanentlyAllow, RuleScope.User, value) with { MatchType = type };

        Assert.Equal(RuleOutcome.Allow, RuleEvaluator.Evaluate([rule], sender, subject, AccountId, Now).Outcome);
    }

    private static RuleEvaluation Evaluate(IEnumerable<RuleCandidate> rules, RuleAction? explicitDecision = null) =>
        RuleEvaluator.Evaluate(rules, "sender@example.test", "Invoice", AccountId, Now, explicitDecision);

    private static RuleCandidate Rule(RuleAction action, RuleScope scope, string value = "sender@example.test") =>
        new(Guid.NewGuid(), action, scope, RuleMatchType.ExactSender, value, scope == RuleScope.SourceAccount ? AccountId : null);
}
