using MailWinnow.Web.Components.Layout;

namespace MailWinnow.Tests.Review;

public sealed class NavigationCountLoaderTests
{
    [Fact]
    public async Task OpenSessionReflectsWorkerStyleIncreasesAndDecreaseToZero()
    {
        var state = new NavigationCountState();
        var inboxValues = new Queue<int>([2, 5, 0]);
        var reviewValues = new Queue<(int Messages, int Senders)>([(3, 2), (7, 4), (0, 0)]);
        using var cancellation = new CancellationTokenSource();
        var waits = 0;

        await NavigationCountLoader.RunAsync(
            _ =>
            {
                state.SetInboxCount(inboxValues.Dequeue());
                return Task.CompletedTask;
            },
            _ =>
            {
                var value = reviewValues.Dequeue();
                state.SetReviewCounts(value.Messages, value.Senders);
                return Task.CompletedTask;
            },
            _ =>
            {
                if (++waits == 3)
                {
                    cancellation.Cancel();
                }

                return Task.CompletedTask;
            },
            cancellation.Token);

        Assert.Equal(0, state.InboxCount);
        Assert.Equal(0, state.ReviewMessageCount);
        Assert.Equal(0, state.ReviewSenderCount);
        Assert.Equal(3, waits);
    }

    [Fact]
    public async Task TransientFailurePreservesCountsAndLaterRefreshRecovers()
    {
        var state = new NavigationCountState();
        state.SetInboxCount(4);
        var attempts = 0;
        using var cancellation = new CancellationTokenSource();

        await NavigationCountLoader.RunAsync(
            _ =>
            {
                if (++attempts == 1)
                {
                    throw new InvalidOperationException("temporarily unavailable");
                }

                state.SetInboxCount(8);
                return Task.CompletedTask;
            },
            _ => Task.CompletedTask,
            _ =>
            {
                if (attempts == 2)
                {
                    cancellation.Cancel();
                }

                return Task.CompletedTask;
            },
            cancellation.Token);

        Assert.Equal(2, attempts);
        Assert.Equal(8, state.InboxCount);
    }

    [Fact]
    public async Task RefreshCallbacksRemainBoundToAuthenticatedSessionUser()
    {
        const string sessionUser = "household-one";
        var observedUsers = new List<string>();
        using var cancellation = new CancellationTokenSource();
        var cycles = 0;

        await NavigationCountLoader.RunAsync(
            _ =>
            {
                observedUsers.Add(sessionUser);
                return Task.CompletedTask;
            },
            _ =>
            {
                observedUsers.Add(sessionUser);
                return Task.CompletedTask;
            },
            _ =>
            {
                if (++cycles == 2)
                {
                    cancellation.Cancel();
                }

                return Task.CompletedTask;
            },
            cancellation.Token);

        Assert.Equal(4, observedUsers.Count);
        Assert.All(observedUsers, user => Assert.Equal(sessionUser, user));
        Assert.DoesNotContain("household-two", observedUsers);
    }
}
