using MailWinnow.Web.Components.Layout;

namespace MailWinnow.Tests.Review;

public sealed class NavigationCountLoaderTests
{
    [Fact]
    public async Task DelayedLoadersStartTogetherAndUpdateIndependently()
    {
        var state = new NavigationCountState();
        var inboxCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reviewCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inboxStarted = false;
        var reviewStarted = false;

        NavigationCountLoader.Start(async () =>
        {
            inboxStarted = true;
            await inboxCompletion.Task;
            state.SetInboxCount(7);
        }, async () =>
        {
            reviewStarted = true;
            await reviewCompletion.Task;
            state.SetReviewCounts(11, 3);
        });

        Assert.True(inboxStarted);
        Assert.True(reviewStarted);

        reviewCompletion.SetResult();
        await WaitForAsync(() => state.ReviewMessageCount == 11);
        Assert.Equal(0, state.InboxCount);
        Assert.Equal(3, state.ReviewSenderCount);

        inboxCompletion.SetResult();
        await WaitForAsync(() => state.InboxCount == 7);
    }

    [Fact]
    public async Task FailedLoaderDoesNotPreventOtherLoaderFromUpdating()
    {
        var state = new NavigationCountState();
        var reviewCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        NavigationCountLoader.Start(() => Task.FromException(new InvalidOperationException("unavailable")), async () =>
        {
            await reviewCompletion.Task;
            state.SetReviewCounts(5, 2);
        });

        reviewCompletion.SetResult();
        await WaitForAsync(() => state.ReviewMessageCount == 5);

        Assert.Equal(0, state.InboxCount);
        Assert.Equal(2, state.ReviewSenderCount);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(1);
        }

        Assert.True(condition());
    }
}
