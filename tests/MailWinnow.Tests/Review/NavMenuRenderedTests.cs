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
        var inbox = new DeferredNavigationCounts();
        var reviews = inbox;
        ConfigureAuthenticatedServices(inbox, reviews);

        var menu = Render<NavMenu>();

        AssertAuthenticatedItemsRemainVisible(menu);
        Assert.Empty(menu.FindAll(".sidebar-count"));

        reviews.CompleteRecent(new(3, 2));

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
        var inbox = new DeferredNavigationCounts();
        var reviews = inbox;
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

    private void ConfigureAuthenticatedServices(DeferredNavigationCounts inbox, DeferredNavigationCounts reviews)
    {
        Services.AddLogging(builder => builder.AddDebug());
        Services.AddSingleton(new NavigationCountState());
        Services.AddSingleton<INavigationCountService>(inbox);

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

    private sealed class DeferredNavigationCounts : INavigationCountService
    {
        private readonly TaskCompletionSource<int?> count = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<ReviewNavigationCounts> recent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<int?> GetInboxCountAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default) => count.Task;
        public Task<ReviewNavigationCounts> GetReviewCountsAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default) => recent.Task;
        public void CompleteCount(int value) => count.SetResult(value);
        public void FailCount() => count.SetException(new InvalidOperationException("Inbox count failed"));
        public void CompleteRecent(ReviewNavigationCounts value) => recent.SetResult(value);
        public void FailRecent() => recent.SetException(new InvalidOperationException("Review counts failed"));
    }
}
