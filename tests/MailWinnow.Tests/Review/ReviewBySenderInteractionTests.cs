using System.Security.Claims;
using Bunit;
using Bunit.TestDoubles;
using MailWinnow.Core.Rules;
using MailWinnow.Infrastructure.Rules;
using MailWinnow.Web.Components.Layout;
using MailWinnow.Web.Components.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace MailWinnow.Tests.Review;

public sealed class ReviewBySenderInteractionTests : BunitContext
{
    [Fact]
    public void ExpandingSenderOnlyRendersNewestFirstRowsAndPreviewFetchesOnlyOpenedMessage()
    {
        var older = ReviewItem("Grouped sender", DateTimeOffset.Parse("2026-08-18T09:00:00Z"));
        var newer = ReviewItem("Grouped sender", DateTimeOffset.Parse("2026-08-19T09:00:00Z"));
        var reviews = new StaticReviews([older, newer]);
        var previews = new RecordingPreviews();
        ConfigureServices(reviews, previews);

        var page = Render<ReviewBySender>();

        page.WaitForAssertion(() => Assert.Single(page.FindAll("button"), button => button.TextContent.Trim() == "View messages"));
        Assert.Empty(page.FindAll(".sender-message-row"));
        Assert.Empty(previews.ReadIds);

        page.FindAll("button").Single(button => button.TextContent.Trim() == "View messages").Click();

        page.WaitForAssertion(() =>
        {
            var rows = page.FindAll(".sender-message-row");
            Assert.Collection(rows,
                row => Assert.Contains("Subject " + newer.Id, row.TextContent),
                row => Assert.Contains("Subject " + older.Id, row.TextContent));
            Assert.Equal("true", page.FindAll("button").Single(button => button.TextContent.Trim() == "Hide messages").GetAttribute("aria-expanded"));
        });
        Assert.Empty(previews.ReadIds);

        page.FindAll(".sender-message-row button").First().Click();
        page.WaitForAssertion(() => Assert.Equal([newer.Id], previews.ReadIds));

        page.FindAll(".sender-message-row button").Last().Click();
        page.WaitForAssertion(() => Assert.Equal([newer.Id, older.Id], previews.ReadIds));
    }

    private void ConfigureServices(IMessageReviewService reviews, IReviewMessagePreviewService previews)
    {
        Services.AddSingleton(new NavigationCountState());
        Services.AddSingleton(reviews);
        Services.AddSingleton(previews);
        Services.AddSingleton<IReviewDecisionQueue, NoopDecisionQueue>();

        var authorization = AddAuthorization();
        authorization.SetAuthorized("household-user");
    }

    private static MessageReviewItem ReviewItem(string sender, DateTimeOffset receivedUtc)
    {
        var id = Guid.NewGuid();
        return new MessageReviewItem(id, sender, "Subject " + id, "Account", receivedUtc, "", RuleOutcome.Pending, "", "", "");
    }

    private sealed class StaticReviews(IReadOnlyList<MessageReviewItem> items) : IMessageReviewService
    {
        public Task<IReadOnlyList<MessageReviewItem>> GetRecentAsync(ClaimsPrincipal user, MessageReviewFilter filter, CancellationToken cancellationToken = default) =>
            Task.FromResult(items);

        public Task<IReadOnlyList<MessageReviewGroup>> GetBySenderAsync(ClaimsPrincipal user, MessageReviewFilter filter, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<MessageReviewGroup>> GetBySubjectAsync(ClaimsPrincipal user, MessageReviewFilter filter, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ReviewRule>> GetRulesAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingPreviews : IReviewMessagePreviewService
    {
        public List<Guid> ReadIds { get; } = [];

        public Task<ReviewPreviewResult> ReadAsync(ClaimsPrincipal user, Guid reviewItemId, CancellationToken cancellationToken = default)
        {
            ReadIds.Add(reviewItemId);
            return Task.FromResult(new ReviewPreviewResult(true,
                new MailWinnow.Infrastructure.Rules.ReviewMessagePreview("from@example.test", "to@example.test", "Preview", DateTimeOffset.UtcNow, "<p>Safe</p>")));
        }
    }

    private sealed class NoopDecisionQueue : IReviewDecisionQueue
    {
        public Task<ReviewDecisionQueueResult> QueueAsync(ClaimsPrincipal actor, RuleAction action, RuleMatchType matchType,
            string matchValue, int? retentionDays, IReadOnlyList<Guid> messageIds, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReviewDecisionQueueResult(true, "Accepted"));

        public Task<ReviewDecisionQueueMetrics> GetMetricsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReviewDecisionQueueMetrics(0, 0, 0, 0, null));
    }
}
