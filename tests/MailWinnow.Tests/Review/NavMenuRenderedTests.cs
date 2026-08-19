using System.Security.Claims;
using Bunit;
using Bunit.TestDoubles;
using MailWinnow.Core.Rules;
using MailWinnow.Infrastructure.Mailboxes;
using MailWinnow.Infrastructure.Rules;
using MailWinnow.Web.Components.Layout;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MailWinnow.Tests.Review;

public sealed class NavMenuRenderedTests : BunitContext
{
    [Fact]
    public void AuthenticatedNavigationRendersWhileCountsArePendingAndUpdatesBadgesIndependently()
    {
        var inbox = new DeferredInboxReader();
        var reviews = new DeferredMessageReviewService();
        ConfigureAuthenticatedServices(inbox, reviews);

        var menu = Render<NavMenu>();

        AssertAuthenticatedItemsRemainVisible(menu);
        Assert.Empty(menu.FindAll(".sidebar-count"));

        reviews.CompleteRecent(
        [
            ReviewItem("sender-one@example.test"),
            ReviewItem("sender-two@example.test"),
            ReviewItem("sender-one@example.test")
        ]);

        menu.WaitForAssertion(() =>
        {
            AssertAuthenticatedItemsRemainVisible(menu);
            Assert.Contains(menu.FindAll(".sidebar-count"), badge =>
                badge.TextContent == "3" && badge.GetAttribute("aria-label") == "3 pending messages");
            Assert.Contains(menu.FindAll(".sidebar-count"), badge =>
                badge.TextContent == "2" && badge.GetAttribute("aria-label") == "2 pending senders");
            Assert.DoesNotContain(menu.FindAll(".sidebar-count"), badge => badge.GetAttribute("aria-label")?.Contains("inbox") == true);
        });

        inbox.CompleteCount(9);

        menu.WaitForAssertion(() =>
        {
            AssertAuthenticatedItemsRemainVisible(menu);
            Assert.Contains(menu.FindAll(".sidebar-count"), badge =>
                badge.TextContent == "9" && badge.GetAttribute("aria-label") == "9 inbox messages");
            Assert.Contains(menu.FindAll(".sidebar-count"), badge =>
                badge.TextContent == "3" && badge.GetAttribute("aria-label") == "3 pending messages");
            Assert.Contains(menu.FindAll(".sidebar-count"), badge =>
                badge.TextContent == "2" && badge.GetAttribute("aria-label") == "2 pending senders");
        });
    }

    [Fact]
    public void AuthenticatedNavigationRendersWhenCountServicesFail()
    {
        var inbox = new DeferredInboxReader();
        var reviews = new DeferredMessageReviewService();
        ConfigureAuthenticatedServices(inbox, reviews);

        var menu = Render<NavMenu>();

        inbox.FailCount();
        reviews.FailRecent();

        menu.WaitForAssertion(() =>
        {
            AssertAuthenticatedItemsRemainVisible(menu);
            Assert.Empty(menu.FindAll(".sidebar-count"));
        });
    }

    private void ConfigureAuthenticatedServices(DeferredInboxReader inbox, DeferredMessageReviewService reviews)
    {
        Services.AddLogging(builder => builder.AddDebug());
        Services.AddSingleton(new NavigationCountState());
        Services.AddSingleton<IInboxReaderService>(inbox);
        Services.AddSingleton<IMessageReviewService>(reviews);

        var authorization = AddAuthorization();
        authorization.SetAuthorized("household-user");
    }

    private static void AssertAuthenticatedItemsRemainVisible(IRenderedComponent<NavMenu> menu)
    {
        var text = menu.Markup;
        Assert.Contains("Inbox", text);
        Assert.Contains("Review - Recent", text);
        Assert.Contains("Review - Sender", text);
        Assert.Contains("Rules", text);
        Assert.Contains("Mailboxes", text);
        Assert.Contains("Sign out", text);
    }

    private static MessageReviewItem ReviewItem(string sender) => new(Guid.NewGuid(), sender, "Subject", "Account",
        DateTimeOffset.UtcNow, "Source: enabled", RuleOutcome.Pending, "Awaiting review", "No matching reusable rule",
        "Destination retention: forever");

    private sealed class DeferredInboxReader : IInboxReaderService
    {
        private readonly TaskCompletionSource<InboxLoadResult<int>> count = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<InboxLoadResult<int>> CountAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default) => count.Task;

        public Task<InboxLoadResult<IReadOnlyList<InboxMessageSummary>>> ListAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<InboxLoadResult<InboxMessageContent>> ReadAsync(ClaimsPrincipal user, uint uid, uint uidValidity, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void CompleteCount(int value) => count.SetResult(new(true, value));

        public void FailCount() => count.SetException(new InvalidOperationException("Inbox count failed"));
    }

    private sealed class DeferredMessageReviewService : IMessageReviewService
    {
        private readonly TaskCompletionSource<IReadOnlyList<MessageReviewItem>> recent = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<MessageReviewItem>> GetRecentAsync(ClaimsPrincipal user, MessageReviewFilter filter, CancellationToken cancellationToken = default) =>
            recent.Task;

        public Task<IReadOnlyList<MessageReviewGroup>> GetBySenderAsync(ClaimsPrincipal user, MessageReviewFilter filter, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<MessageReviewGroup>> GetBySubjectAsync(ClaimsPrincipal user, MessageReviewFilter filter, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ReviewRule>> GetRulesAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void CompleteRecent(IReadOnlyList<MessageReviewItem> items) => recent.SetResult(items);

        public void FailRecent() => recent.SetException(new InvalidOperationException("Review counts failed"));
    }
}
