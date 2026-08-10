using MailWinnow.Web.Components.Layout;

namespace MailWinnow.Tests.Review;

public sealed class NavigationCountStateTests
{
    [Fact]
    public void CountChangesArePublishedAndNegativeValuesAreClamped()
    {
        var state = new NavigationCountState();
        var notifications = 0;
        state.Changed += () => notifications++;

        state.SetInboxCount(4);
        state.SetReviewCounts(7, 3);

        Assert.Equal(4, state.InboxCount);
        Assert.Equal(7, state.ReviewMessageCount);
        Assert.Equal(3, state.ReviewSenderCount);
        Assert.Equal(2, notifications);

        state.SetInboxCount(-1);
        state.SetReviewCounts(-2, -3);

        Assert.Equal(0, state.InboxCount);
        Assert.Equal(0, state.ReviewMessageCount);
        Assert.Equal(0, state.ReviewSenderCount);
        Assert.Equal(4, notifications);
    }
}
