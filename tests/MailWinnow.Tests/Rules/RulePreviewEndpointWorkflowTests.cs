using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using MailWinnow.Core.Rules;
using MailWinnow.Infrastructure.Rules;
using MailWinnow.Infrastructure.Security;
using MailWinnow.Web.Components;
using MailWinnow.Web.Components.Layout;
using MailWinnow.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using MailWinnow.Infrastructure.Persistence;
using MailWinnow.Infrastructure.Mailboxes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Encodings.Web;

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
    public async Task Create_RealWorkflowIsReadOnlyUntilConfirmationAndReplaySafe()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new MailWinnowDbContext(new DbContextOptionsBuilder<MailWinnowDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var mailbox = new SourceMailbox { OwnerUserId = Owner, DisplayName = "Source", Host = "imap.test", Port = 993, UseSsl = true, Username = "owner", ProtectedCredential = "protected", SelectedFoldersJson = "[]" };
        var header = new SourceMessageHeader { SourceMailboxId = mailbox.Id, FolderName = "INBOX", UidValidity = 1, Uid = 1, From = "sender@example.test", Subject = "Subject", EvaluationOutcome = RuleOutcome.Pending };
        db.AddRange(mailbox, header);
        await db.SaveChangesAsync();
        var deliveries = new RecordingDeliveryService();
        var evaluator = new RuleEvaluationService(db, deliveries);
        var rules = new RuleManagementService(db, evaluator);
        var protection = new EphemeralDataProtectionProvider();
        var request = Request();

        var token = TokenFrom(await ReviewEndpoints.AddRuleAsync(Context(), request, rules, new RuleImpactPreviewService(db), new OwnershipAuthorizer(), protection, default));

        Assert.Empty(await db.MailRules.ToListAsync());
        Assert.Equal(RuleOutcome.Pending, header.EvaluationOutcome);
        Assert.Equal(0, deliveries.QueueCount);

        request.Confirm = true;
        request.PreviewToken = token;
        await ReviewEndpoints.AddRuleAsync(Context(), request, rules, new RuleImpactPreviewService(db), new OwnershipAuthorizer(), protection, default);
        await ReviewEndpoints.AddRuleAsync(Context(), request, rules, new RuleImpactPreviewService(db), new OwnershipAuthorizer(), protection, default);

        Assert.Single(await db.MailRules.ToListAsync());
        Assert.Equal(RuleOutcome.Allow, header.EvaluationOutcome);
        Assert.Equal(1, deliveries.QueueCount);
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

    [Fact]
    public async Task RenderedEditForm_SubmitsToMappedPreviewEndpointWithoutWriting()
    {
        var replacedRuleId = Guid.NewGuid();
        var rules = new RecordingRuleManagementService();
        var previews = new RecordingPreviewService(new RuleImpactPreview(1, 0, []));
        await using var app = CreateRenderedApp(rules, previews, replacedRuleId);
        await app.StartAsync();
        using var client = new HttpClient(app.GetTestServer().CreateHandler()) { BaseAddress = new Uri("http://localhost") };
        var pageResponse = await client.GetAsync("/rules");
        var page = await pageResponse.Content.ReadAsStringAsync();
        Assert.Equal(StatusCodes.Status200OK, (int)pageResponse.StatusCode);
        var editForm = Regex.Match(page, "<form method=\"post\" action=\"(?<action>[^\"]+)\" class=\"form-stack mt-3\">(?<fields>.*?)name=\"ReplaceRuleId\" value=\"(?<replace>[^\"]+)\"", RegexOptions.Singleline);
        Assert.True(editForm.Success, "The authenticated Rules page did not render an edit form.");
        Assert.Equal(ReviewEndpoints.RulePreviewPath + "?returnUrl=/rules", editForm.Groups["action"].Value);
        Assert.Equal(replacedRuleId.ToString(), editForm.Groups["replace"].Value);
        var antiforgery = Regex.Match(editForm.Groups["fields"].Value, "<input(?=[^>]*name=\"(?<name>__RequestVerificationToken)\")(?=[^>]*value=\"(?<value>[^\"]+)\")[^>]*>");
        Assert.True(antiforgery.Success, "The rendered edit form did not contain an antiforgery token.");
        var antiforgeryCookie = pageResponse.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith(".AspNetCore.Antiforgery.", StringComparison.Ordinal));
        client.DefaultRequestHeaders.Add("Cookie", antiforgeryCookie.Split(';')[0]);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Action"] = nameof(RuleAction.PermanentlyBlock),
            ["Scope"] = nameof(RuleScope.User),
            ["MatchType"] = nameof(RuleMatchType.ExactSender),
            ["MatchValue"] = "sender@example.test",
            ["ReplaceRuleId"] = editForm.Groups["replace"].Value,
            [antiforgery.Groups["name"].Value] = antiforgery.Groups["value"].Value
        });

        var response = await client.PostAsync(editForm.Groups["action"].Value, content);

        Assert.Equal(StatusCodes.Status302Found, (int)response.StatusCode);
        Assert.StartsWith("/rules?preview=", response.Headers.Location?.OriginalString);
        Assert.Equal(replacedRuleId, previews.ReplacedRuleId);
        Assert.Equal(0, rules.WriteCount);
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
        context.User = TestPrincipal(owner);
        context.Request.QueryString = new QueryString("?returnUrl=/rules");
        return context;
    }

    private static ClaimsPrincipal TestPrincipal(string owner = Owner) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, owner)], "test"));

    private static string TokenFrom(IResult result)
    {
        var url = RedirectUrl(result);
        var encoded = url[(url.IndexOf("preview=", StringComparison.Ordinal) + "preview=".Length)..];
        return Uri.UnescapeDataString(encoded);
    }

    private static string RedirectUrl(IResult result) =>
        (string)(result.GetType().GetProperty("Url")?.GetValue(result)
            ?? throw new InvalidOperationException("Expected a redirect result."));

    private static WebApplication CreateMappedApp(IRuleManagementService rules, IRuleImpactPreviewService previews)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDataProtection();
        builder.Services.AddAntiforgery();
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(rules);
        builder.Services.AddSingleton(previews);
        builder.Services.AddSingleton<IOwnershipAuthorizer, OwnershipAuthorizer>();
        builder.Services.AddSingleton<IMessageDeliveryService>(new RecordingDeliveryService());
        builder.Services.AddSingleton<IReviewDecisionQueue>(new RecordingReviewDecisionQueue());

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();
        app.MapReviewEndpoints();
        return app;
    }

    private static WebApplication CreateRenderedApp(IRuleManagementService rules, IRuleImpactPreviewService previews, Guid ruleId)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddDataProtection();
        builder.Services.AddAntiforgery();
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddScoped<IMessageReviewService>(_ => new RenderedRulesReviewService(ruleId));
        builder.Services.AddScoped<IMailboxConfigurationService, RenderedRulesMailboxService>();
        builder.Services.AddScoped<IInboxReaderService, RenderedRulesInboxReader>();
        builder.Services.AddScoped<NavigationCountState>();
        builder.Services.AddSingleton(rules);
        builder.Services.AddSingleton(previews);
        builder.Services.AddSingleton<IOwnershipAuthorizer, OwnershipAuthorizer>();
        builder.Services.AddSingleton<IMessageDeliveryService>(new RecordingDeliveryService());
        builder.Services.AddSingleton<IReviewDecisionQueue>(new RecordingReviewDecisionQueue());

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
        app.MapReviewEndpoints();
        return app;
    }

    private sealed class TestAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public TestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(
            AuthenticateResult.Success(new AuthenticationTicket(TestPrincipal(), Scheme.Name)));
    }

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

    private sealed class RenderedRulesReviewService(Guid ruleId) : IMessageReviewService
    {
        public Task<IReadOnlyList<MessageReviewItem>> GetRecentAsync(ClaimsPrincipal user, MessageReviewFilter filter, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MessageReviewItem>>([]);
        public Task<IReadOnlyList<MessageReviewGroup>> GetBySenderAsync(ClaimsPrincipal user, MessageReviewFilter filter, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MessageReviewGroup>>([]);
        public Task<IReadOnlyList<MessageReviewGroup>> GetBySubjectAsync(ClaimsPrincipal user, MessageReviewFilter filter, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MessageReviewGroup>>([]);
        public Task<IReadOnlyList<ReviewRule>> GetRulesAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ReviewRule>>([new(ruleId, RuleAction.PermanentlyAllow, RuleScope.User, RuleMatchType.ExactSender, "sender@example.test", null, null, 7, DateTimeOffset.UtcNow)]);
    }

    private sealed class RenderedRulesMailboxService : IMailboxConfigurationService
    {
        public Task<IReadOnlyList<SourceMailboxSummary>> ListSourcesAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SourceMailboxSummary>>([]);
        public Task<DestinationMailboxSummary?> GetDestinationAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default) => Task.FromResult<DestinationMailboxSummary?>(null);
        public Task<MailboxOperationResult> SaveSourceAsync(ClaimsPrincipal actor, Guid? id, SourceMailboxInput input, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailboxOperationResult> SaveSourceFoldersAsync(ClaimsPrincipal actor, Guid id, IReadOnlyList<string>? selectedFolders, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailboxOperationResult> SetSourceEnabledAsync(ClaimsPrincipal actor, Guid id, bool enabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailboxOperationResult> TestSourceAsync(ClaimsPrincipal actor, Guid id, bool discoverFolders, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailboxOperationResult> SaveDestinationAsync(ClaimsPrincipal actor, DestinationMailboxInput input, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RenderedRulesInboxReader : IInboxReaderService
    {
        public Task<InboxLoadResult<int>> CountAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default) => Task.FromResult(new InboxLoadResult<int>(true, 0));
        public Task<InboxLoadResult<IReadOnlyList<InboxMessageSummary>>> ListAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<InboxLoadResult<InboxMessageContent>> ReadAsync(ClaimsPrincipal user, uint uid, uint uidValidity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RecordingRuleManagementService : IRuleManagementService
    {
        public int AddOrUpdateCount { get; private set; }
        public int ReplaceCount { get; private set; }
        public int WriteCount => AddOrUpdateCount + ReplaceCount;
        public Guid? ReplacedRuleId { get; private set; }

        public Task PersistAsync(MailRule rule, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task AddOrUpdateAsync(MailRule rule, CancellationToken cancellationToken = default) { AddOrUpdateCount++; return Task.CompletedTask; }
        public Task ReplaceAsync(string ownerUserId, Guid ruleId, MailRule replacement, CancellationToken cancellationToken = default) { ReplaceCount++; ReplacedRuleId = ruleId; return Task.CompletedTask; }
        public Task DeleteAsync(string ownerUserId, Guid ruleId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetMessageDecisionAsync(MessageDecision decision, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteMessageDecisionAsync(string ownerUserId, Guid headerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RecordingDeliveryService : IMessageDeliveryService
    {
        public int QueueCount { get; private set; }
        public Task QueueApprovedAsync(string ownerUserId, Guid sourceMessageHeaderId, Guid? approvalRuleId, CancellationToken cancellationToken = default) { QueueCount++; return Task.CompletedTask; }
        public Task DeliverAsync(Guid deliveryId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Guid>> GetDueDeliveryIdsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Guid>> GetDueCleanupIdsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CleanupExpiredAsync(Guid deliveryId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailboxOperationResult> RetryAsync(ClaimsPrincipal principal, Guid deliveryId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RecordingReviewDecisionQueue : IReviewDecisionQueue
    {
        public Task<ReviewDecisionQueueResult> QueueAsync(ClaimsPrincipal actor, RuleAction action, RuleMatchType matchType, string matchValue, int? retentionDays, IReadOnlyList<Guid> messageIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ReviewDecisionQueueMetrics> GetMetricsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ReviewDecisionQueueMetrics(0, 0, 0, 0, null));
    }
}
