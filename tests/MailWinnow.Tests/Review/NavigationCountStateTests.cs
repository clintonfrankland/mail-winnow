using MailWinnow.Web.Components.Layout;

namespace MailWinnow.Tests.Review;

public sealed class NavigationCountStateTests
{
    [Fact]
    public void InFlightRefreshDoesNotUndoCountsChangedByAnAcceptedClick()
    {
        var state = new NavigationCountState();
        state.SetInboxCount(10);
        state.SetReviewCounts(10, 5);
        var inboxRevision = state.InboxRevision;
        var reviewRevision = state.ReviewRevision;
        state.SetInboxCount(9);
        state.SetReviewCounts(8, 4);

        state.ApplyInboxRefresh(10, inboxRevision);
        state.ApplyReviewRefresh(10, 5, reviewRevision);

        Assert.Equal(9, state.InboxCount);
        Assert.Equal(8, state.ReviewMessageCount);
        Assert.Equal(4, state.ReviewSenderCount);
        state.ApplyReviewRefresh(7, 3, state.ReviewRevision);
        Assert.Equal(7, state.ReviewMessageCount);
    }

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
