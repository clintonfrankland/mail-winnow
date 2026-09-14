using System.Security.Claims;
using Bunit;
using Bunit.TestDoubles;
using MailWinnow.Infrastructure.Mailboxes;
using MailWinnow.Web.Components.Layout;
using MailWinnow.Web.Components.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace MailWinnow.Tests.Review;

public sealed class InboxRefreshInteractionTests : BunitContext
{
    [Fact]
    public async Task RefreshAcknowledgesDurableRequestWithoutWaitingForImapAndReaderRemainsUsable()
    {
        var state = Configure();
        var page = Render<Inbox>();
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".message-row")));
        state.NextList = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.Find(".inbox-list header button").ClickAsync(new());
        page.WaitForAssertion(() => Assert.Equal(1, state.Requests));
        Assert.Contains("Mail check requested", page.Markup);
        await page.Find(".message-row").ClickAsync(new());
        page.WaitForAssertion(() => Assert.Contains("Message 1", page.Find(".reader-heading").TextContent));
        Assert.False(page.Find("button[aria-label='Delete this email']").HasAttribute("disabled"));
        Assert.True(state.ReadCalls > 0);
        state.NextList.SetResult(new(true, state.Messages));
        page.WaitForAssertion(() => Assert.Contains("Mail check finished", page.Markup));
        Assert.True(state.ReadersCreated >= 3);
        Assert.True(state.ReadersDisposed >= 3);
    }

    [Fact]
    public async Task BackgroundReloadCannotResurrectOptimisticallyDeletedMessage()
    {
        var state = Configure();
        var page = Render<Inbox>();
        await page.Find(".message-row").ClickAsync(new());
        state.NextList = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.Find(".inbox-list header button").ClickAsync(new());
        await page.Find("button[aria-label='Delete this email']").ClickAsync(new());
        Assert.Empty(page.FindAll(".message-row"));
        Assert.Equal(1, state.Deletions);
        state.NextList.SetResult(new(true, state.Messages)); // stale IMAP snapshot still contains deleted UID
        page.WaitForAssertion(() => Assert.Contains("Mail check finished", page.Markup));
        Assert.Empty(page.FindAll(".message-row"));
        Assert.Null(page.FindAll(".reader-heading").SingleOrDefault());
    }

    [Fact]
    public async Task RefreshClearsPreviewWhenMessageWasRemovedOutsideThisPage()
    {
        var state = Configure();
        var page = Render<Inbox>();
        await page.Find(".message-row").ClickAsync(new());
        state.Messages = [];
        await page.Find(".inbox-list header button").ClickAsync(new());
        page.WaitForAssertion(() => Assert.Empty(page.FindAll(".reader-heading")));
        Assert.Empty(page.FindAll(".message-row"));
    }

    [Fact]
    public async Task AccountFailureIsExplainedWithoutDisablingInboxInteraction()
    {
        var state = Configure();
        state.Status = new(0, 0, 1, 0, 0, null);
        var page = Render<Inbox>();
        await page.Find(".inbox-list header button").ClickAsync(new());
        page.WaitForAssertion(() => Assert.Contains("some accounts or messages could not be processed", page.Markup));
        Assert.False(page.Find(".inbox-list header button").HasAttribute("disabled"));
        await page.Find(".message-row").ClickAsync(new());
        Assert.Single(page.FindAll(".reader-heading"));
    }

    [Fact]
    public async Task RejectedRequestDoesNotStartProgressOrSourceWork()
    {
        var state = Configure();
        state.RequestSucceeded = false;
        var page = Render<Inbox>();
        await page.Find(".inbox-list header button").ClickAsync(new());
        Assert.Contains("No enabled source accounts", page.Markup);
        Assert.Equal(0, state.StatusCalls);
        Assert.Equal(1, state.ListCalls);
    }

    [Fact]
    public async Task NavigationCancelsObservationButNotTheSavedSourceRequest()
    {
        var state = Configure();
        state.NextStatus = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var page = Render<Inbox>();
        await page.Find(".inbox-list header button").ClickAsync(new());
        page.WaitForAssertion(() => Assert.Equal(1, state.StatusCalls));
        await page.InvokeAsync(async () => await page.Instance.DisposeAsync());
        Assert.True(state.StatusToken.IsCancellationRequested);
        Assert.Equal(1, state.Requests);
    }

    [Fact]
    public async Task FailedLocalReloadDoesNotClaimThatInboxWasUpdated()
    {
        var state = Configure();
        var page = Render<Inbox>();
        state.NextList = new(TaskCreationOptions.RunContinuationsAsynchronously);
        state.NextList.SetResult(new(false, null, "Local mailbox unavailable"));
        await page.Find(".inbox-list header button").ClickAsync(new());
        page.WaitForAssertion(() => Assert.Contains("displayed inbox could not be updated", page.Markup));
        Assert.Single(page.FindAll(".message-row"));
    }

    [Fact]
    public async Task NoEnabledSourcesStillReloadsLocalInboxWithoutObservingSourceProgress()
    {
        var state = Configure();
        state.MailboxCount = 0;
        var page = Render<Inbox>();
        state.Messages = [];
        await page.Find(".inbox-list header button").ClickAsync(new());
        page.WaitForAssertion(() => Assert.Empty(page.FindAll(".message-row")));
        Assert.Equal(0, state.StatusCalls);
        Assert.Equal(2, state.ListCalls);
    }

    [Fact]
    public async Task FailedDeletionRestoresRowWithoutStealingNewerSelection()
    {
        var state = Configure();
        state.Messages = [state.Messages[0], new(2, 42, "Another", "Message 2", DateTimeOffset.UtcNow)];
        var page = Render<Inbox>();
        await page.FindAll(".message-row")[0].ClickAsync(new());
        state.NextDeletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var deletion = page.Find("button[aria-label='Delete this email']").ClickAsync(new());
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".message-row")));
        await page.Find(".message-row").ClickAsync(new());
        state.NextDeletion.SetResult(new(false, "Queue unavailable"));
        await deletion;
        Assert.Equal(2, page.FindAll(".message-row").Count);
        Assert.Contains("Message 2", page.Find(".reader-heading").TextContent);
    }

    private FixtureState Configure()
    {
        var state = new FixtureState();
        Services.AddSingleton(new NavigationCountState());
        Services.AddScoped<IInboxReaderService>(_ => new ScopedReader(state));
        Services.AddSingleton<IInboxRefreshService>(new RefreshRequests(state));
        Services.AddSingleton<IInboxDeletionQueue>(new Deletions(state));
        AddAuthorization().SetAuthorized("owner");
        return state;
    }

    private sealed class FixtureState
    {
        public IReadOnlyList<InboxMessageSummary> Messages = [new(1, 42, "Sender", "Message 1", DateTimeOffset.UtcNow, "1 day")];
        public TaskCompletionSource<InboxLoadResult<IReadOnlyList<InboxMessageSummary>>>? NextList;
        public TaskCompletionSource<InboxRefreshStatus>? NextStatus;
        public InboxRefreshStatus Status = new(0, 0, 0, 0, 0, null);
        public bool RequestSucceeded = true;
        public int MailboxCount = 1;
        public TaskCompletionSource<MailboxOperationResult>? NextDeletion;
        public CancellationToken StatusToken;
        public int Requests, StatusCalls, ListCalls, ReadCalls, Deletions, ReadersCreated, ReadersDisposed;
    }

    private sealed class ScopedReader : IInboxReaderService, IDisposable
    {
        private readonly FixtureState _state;
        public ScopedReader(FixtureState state) { _state = state; state.ReadersCreated++; }
        public Task<InboxLoadResult<int>> CountAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default) => Task.FromResult(new InboxLoadResult<int>(true, _state.Messages.Count));
        public async Task<InboxLoadResult<IReadOnlyList<InboxMessageSummary>>> ListAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default)
        {
            _state.ListCalls++;
            if (_state.NextList is { } pending) return await pending.Task.WaitAsync(cancellationToken);
            return new(true, _state.Messages);
        }
        public Task<InboxLoadResult<InboxMessageContent>> ReadAsync(ClaimsPrincipal user, uint uid, uint uidValidity, CancellationToken cancellationToken = default)
        {
            _state.ReadCalls++;
            return Task.FromResult(new InboxLoadResult<InboxMessageContent>(true, new(uid, "Sender", "Owner", "Message " + uid, DateTimeOffset.UtcNow, "<p>Message</p>", "<p>Message</p>", false)));
        }
        public void Dispose() => _state.ReadersDisposed++;
    }

    private sealed class RefreshRequests(FixtureState state) : IInboxRefreshService
    {
        public Task<InboxRefreshRequest> RequestAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
        {
            state.Requests++;
            return Task.FromResult(new InboxRefreshRequest(state.RequestSucceeded, state.RequestSucceeded ? "Queued" : "No enabled source accounts", DateTimeOffset.UtcNow, state.MailboxCount));
        }
        public async Task<InboxRefreshStatus> GetStatusAsync(ClaimsPrincipal actor, DateTimeOffset requestedUtc, CancellationToken cancellationToken = default)
        {
            state.StatusCalls++;
            state.StatusToken = cancellationToken;
            return state.NextStatus is {} pending ? await pending.Task.WaitAsync(cancellationToken) : state.Status;
        }
    }

    private sealed class Deletions(FixtureState state) : IInboxDeletionQueue
    {
        public Task<MailboxOperationResult> QueueAsync(ClaimsPrincipal actor, uint uid, uint uidValidity, CancellationToken cancellationToken = default)
        {
            state.Deletions++;
            return state.NextDeletion?.Task ?? Task.FromResult(new MailboxOperationResult(true, "Queued"));
        }
    }
}
