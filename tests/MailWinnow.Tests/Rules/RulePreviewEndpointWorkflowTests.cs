using System.Security.Claims;
using System.Text.Json;
using MailWinnow.Core.Rules;
using MailWinnow.Infrastructure.Rules;
using MailWinnow.Infrastructure.Security;
using MailWinnow.Web.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;

namespace MailWinnow.Tests.Rules;

public sealed class RulePreviewEndpointWorkflowTests
{
    private const string Owner = "owner";

    [Fact]
    public async Task Create_PreviewIsReadOnlyAndConfirmationPersistsExactlyOnce()
    {
        var rules = new RecordingRuleManagementService();
        var previews = new RecordingPreviewService(new RuleImpactPreview(1, 0, [new("sender@example.test", "Subject")]));
        var protection = new EphemeralDataProtectionProvider();
        var request = Request();

        var previewResult = await ReviewEndpoints.AddRuleAsync(Context(), request, rules, previews, new OwnershipAuthorizer(), protection, default);
        var token = TokenFrom(previewResult);

        Assert.Equal(1, previews.CallCount);
        Assert.Equal(0, rules.WriteCount);

        request.Confirm = true;
        request.PreviewToken = token;
        var confirmationResult = await ReviewEndpoints.AddRuleAsync(Context(), request, rules, previews, new OwnershipAuthorizer(), protection, default);

        Assert.Equal(1, rules.AddOrUpdateCount);
        Assert.Equal(0, rules.ReplaceCount);
        Assert.Equal(1, rules.WriteCount);
        Assert.Contains("saved=Rule%20created", RedirectUrl(confirmationResult));
    }

    [Fact]
    public async Task Edit_PreviewThenConfirmationReplacesExactlyOnce()
    {
        var replacedRuleId = Guid.NewGuid();
        var rules = new RecordingRuleManagementService();
        var previews = new RecordingPreviewService(new RuleImpactPreview(0, 1, []));
        var protection = new EphemeralDataProtectionProvider();
        var request = Request();
        request.Action = RuleAction.PermanentlyBlock;
        request.ReplaceRuleId = replacedRuleId;

        var token = TokenFrom(await ReviewEndpoints.AddRuleAsync(Context(), request, rules, previews, new OwnershipAuthorizer(), protection, default));
        Assert.Equal(replacedRuleId, previews.ReplacedRuleId);
        Assert.Equal(0, rules.WriteCount);

        request.Confirm = true;
        request.PreviewToken = token;
        await ReviewEndpoints.AddRuleAsync(Context(), request, rules, previews, new OwnershipAuthorizer(), protection, default);

        Assert.Equal(0, rules.AddOrUpdateCount);
        Assert.Equal(1, rules.ReplaceCount);
        Assert.Equal(replacedRuleId, rules.ReplacedRuleId);
        Assert.Equal(1, rules.WriteCount);
    }

    [Theory]
    [InlineData("tampered")]
    [InlineData("changed-input")]
    [InlineData("changed-owner")]
    [InlineData("expired")]
    public async Task ConfirmationRejectsInvalidPreviewWithoutWriting(string invalidation)
    {
        var rules = new RecordingRuleManagementService();
        var previews = new RecordingPreviewService(new RuleImpactPreview(0, 0, []));
        var protection = new EphemeralDataProtectionProvider();
        var request = Request();
        var token = TokenFrom(await ReviewEndpoints.AddRuleAsync(Context(), request, rules, previews, new OwnershipAuthorizer(), protection, default));
        var context = Context();

        if (invalidation == "tampered") token += "x";
        if (invalidation == "changed-input") request.MatchValue = "changed@example.test";
        if (invalidation == "changed-owner") context = Context("other-owner");
        if (invalidation == "expired")
        {
            var protector = protection.CreateProtector("MailWinnow.RulePreview.v1").ToTimeLimitedDataProtector();
            var snapshot = ReviewEndpoints.Snapshot.From(request, ReviewEndpoints.BuildRule(Owner, request, DateTimeOffset.UtcNow), Owner);
            token = protector.Protect(JsonSerializer.Serialize(snapshot), DateTimeOffset.UtcNow.AddMinutes(-1));
        }

        request.Confirm = true;
        request.PreviewToken = token;
        var result = await ReviewEndpoints.AddRuleAsync(context, request, rules, previews, new OwnershipAuthorizer(), protection, default);

        Assert.Equal(0, rules.WriteCount);
        Assert.Contains("error=", RedirectUrl(result));
    }

    private static ReviewEndpoints.RuleRequest Request() => new()
    {
        Action = RuleAction.PermanentlyAllow,
        Scope = RuleScope.User,
        MatchType = RuleMatchType.ExactSender,
        MatchValue = "sender@example.test",
        RetentionDays = 7
    };

    private static DefaultHttpContext Context(string owner = Owner)
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, owner)], "test"));
        context.Request.QueryString = new QueryString("?returnUrl=/rules");
        return context;
    }

    private static string TokenFrom(IResult result)
    {
        var url = RedirectUrl(result);
        var encoded = url[(url.IndexOf("preview=", StringComparison.Ordinal) + "preview=".Length)..];
        return Uri.UnescapeDataString(encoded);
    }

    private static string RedirectUrl(IResult result) =>
        (string)(result.GetType().GetProperty("Url")?.GetValue(result)
            ?? throw new InvalidOperationException("Expected a redirect result."));

    private sealed class RecordingPreviewService(RuleImpactPreview result) : IRuleImpactPreviewService
    {
        public int CallCount { get; private set; }
        public Guid? ReplacedRuleId { get; private set; }

        public Task<RuleImpactPreview> PreviewAsync(string ownerUserId, MailRule proposal, Guid? replacedRuleId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        {
            CallCount++;
            ReplacedRuleId = replacedRuleId;
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingRuleManagementService : IRuleManagementService
    {
        public int AddOrUpdateCount { get; private set; }
        public int ReplaceCount { get; private set; }
        public int WriteCount => AddOrUpdateCount + ReplaceCount;
        public Guid? ReplacedRuleId { get; private set; }

        public Task AddOrUpdateAsync(MailRule rule, CancellationToken cancellationToken = default) { AddOrUpdateCount++; return Task.CompletedTask; }
        public Task ReplaceAsync(string ownerUserId, Guid ruleId, MailRule replacement, CancellationToken cancellationToken = default) { ReplaceCount++; ReplacedRuleId = ruleId; return Task.CompletedTask; }
        public Task DeleteAsync(string ownerUserId, Guid ruleId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetMessageDecisionAsync(MessageDecision decision, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteMessageDecisionAsync(string ownerUserId, Guid headerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
