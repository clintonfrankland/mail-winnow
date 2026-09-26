using System.Security.Claims;
using Bunit;
using MailWinnow.Infrastructure.Mailboxes;
using MailWinnow.Infrastructure.Outgoing;
using MailWinnow.Web.Components.Layout;
using MailWinnow.Web.Components.Mail;
using MailWinnow.Web.Components.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace MailWinnow.Tests.Outgoing;

public sealed class OutgoingUiTests : BunitContext
{
    [Fact]
    public async Task SendSavesEditedSnapshotThenQueuesWithoutClaimingSmtpDelivery()
    {
        var service = Configure();
        var composer = Render<MessageComposer>(parameters => parameters.Add(component => component.Draft, service.Draft));
        await composer.Find("textarea[aria-label='Message']").InputAsync(new() { Value = "My reply\n\n> original" });
        await Button(composer, "Send").ClickAsync(new());
        Assert.Equal("My reply\n\n> original", service.LastSave!.Body);
        Assert.Equal(service.Draft.Revision, service.QueuedRevision);
        Assert.Equal(1, service.QueueCalls);
        Assert.Contains("Queued for sending", composer.Markup);
        Assert.Contains("does not yet mean it has been sent", composer.Markup);
        Assert.Empty(composer.FindAll("textarea"));
    }

    [Fact]
    public async Task FailedSaveKeepsTextVisibleAndDoesNotQueueOrClose()
    {
        var service = Configure();
        service.SaveFailure = new OutgoingMailException("This draft changed in another tab. Reopen the saved draft.");
        var closed = false;
        var composer = Render<MessageComposer>(parameters => parameters.Add(component => component.Draft, service.Draft)
            .Add(component => component.Closed, () => closed = true));
        await composer.Find("input[aria-label='Subject']").InputAsync(new() { Value = "Keep my local edits" });
        await Button(composer, "Send").ClickAsync(new());
        Assert.Contains("another tab", composer.Find("[role='alert']").TextContent);
        Assert.Equal("Keep my local edits", composer.Find("input[aria-label='Subject']").GetAttribute("value"));
        Assert.Equal(0, service.QueueCalls);
        Assert.False(closed);
        Assert.Contains("Unsaved changes", composer.Markup);
    }

    [Fact]
    public async Task EditsMadeDuringAutosaveAreSavedByTheNextExplicitSave()
    {
        var service = Configure();
        service.PendingSave = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var composer = Render<MessageComposer>(parameters => parameters.Add(component => component.Draft, service.Draft));
        await composer.Find("input[aria-label='Subject']").InputAsync(new() { Value = "First change" });
        await service.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("First change", service.LastSave?.Subject);
        await composer.Find("input[aria-label='Subject']").InputAsync(new() { Value = "Second change" });
        var pending = service.PendingSave;
        service.PendingSave = null;
        pending.SetResult(service.Draft with { Subject = "First change", Revision = 2 });
        await Button(composer, "Save Draft").ClickAsync(new());
        Assert.Equal("Second change", service.LastSave!.Subject);
        Assert.Equal(2, service.LastSave.Revision);
        Assert.Contains("Draft saved", composer.Markup);
    }

    [Fact]
    public async Task SaveAndCloseMakesDraftRecoverableWithoutSendingAndDiscardRequiresConfirmation()
    {
        var service = Configure();
        var closed = false;
        var composer = Render<MessageComposer>(parameters => parameters.Add(component => component.Draft, service.Draft)
            .Add(component => component.Closed, () => closed = true));
        await composer.Find("input[aria-label='To']").InputAsync(new() { Value = "edited@example.test" });
        await Button(composer, "Save and close").ClickAsync(new());
        Assert.True(closed);
        Assert.Equal("edited@example.test", service.Draft.To);
        Assert.Equal(0, service.QueueCalls);
        await Button(composer, "Discard").ClickAsync(new());
        Assert.Equal(0, service.DiscardCalls);
        await Button(composer, "Discard draft permanently").ClickAsync(new());
        Assert.Equal(1, service.DiscardCalls);
    }

    [Fact]
    public async Task QueueFailurePreservesSavedDraftAndExplainsUncertainty()
    {
        var service = Configure();
        service.QueueFailure = new IOException("connection failed");
        var composer = Render<MessageComposer>(parameters => parameters.Add(component => component.Draft, service.Draft));
        await Button(composer, "Send").ClickAsync(new());
        Assert.Contains("Check Drafts & Outbox before repeating Send", composer.Find("[role='alert']").TextContent);
        Assert.Single(composer.FindAll("textarea"));
        Assert.DoesNotContain("Queued for sending", composer.Markup);
    }

    [Fact]
    public async Task UnknownSenderRequiresExplicitChoiceAndOriginalAttachmentsAreNotAdded()
    {
        var service = Configure();
        service.Draft = service.Draft with { SendingAccountId = null };
        var composer = Render<MessageComposer>(parameters => parameters.Add(component => component.Draft, service.Draft));
        Assert.True(Button(composer, "Send").HasAttribute("disabled"));
        Assert.Empty(composer.FindAll(".composer-attachments li"));
        Assert.Contains("Original attachments are not included", composer.Markup);
        await composer.Find("select[aria-label='From']").ChangeAsync(new() { Value = service.Account.Id.ToString() });
        Assert.False(Button(composer, "Send").HasAttribute("disabled"));
    }

    [Fact]
    public void OutboxDistinguishesUnknownOutcomeAndSentCopyFailureWithoutOfferingResend()
    {
        var service = Configure();
        service.Outbox = [
            new(Guid.NewGuid(), service.Draft.Id, "Ambiguous", OutgoingState.OutcomeUnknown, DateTimeOffset.UtcNow, null, "smtp-outcome-unknown", null, false),
            new(Guid.NewGuid(), service.Draft.Id, "Delivered", OutgoingState.Sent, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, "Retrying local copy", false)];
        var page = Render<Outbox>();
        Assert.Contains("Outcome unknown", page.Markup);
        Assert.Contains("will not be resent automatically", page.Markup);
        Assert.Contains("missing Sent copy will not cause a resend", page.Markup);
        Assert.DoesNotContain(page.FindAll("button"), button => button.TextContent.Contains("Retry sending"));
    }

    [Fact]
    public async Task ReplyCapturesExactOriginalIdentityAndRemainsOpenAcrossSelectionAndDeletion()
    {
        var service = Configure();
        ConfigureInbox();
        var page = Render<Inbox>();
        await page.FindAll(".message-row")[0].ClickAsync(new());
        await Button(page, "Reply All").ClickAsync(new());
        Assert.True(service.ReplyAll);
        Assert.Equal(new ReplySource(InboxFake.Destination, "INBOX", "identity", 1, 42), service.ReplySource);
        await page.Find("textarea[aria-label='Message']").InputAsync(new() { Value = "Independent draft" });
        await page.FindAll(".message-row")[1].ClickAsync(new());
        await page.Find("button[aria-label='Delete this email']").ClickAsync(new());
        Assert.Single(page.FindAll(".message-composer"));
        await Button(page, "Save Draft").ClickAsync(new());
        Assert.Equal("Independent draft", service.Draft.Body);
        Assert.Equal((uint)1, service.ReplySource!.Uid);
    }

    [Fact]
    public async Task InboxRemainsUsableWhileDurableQueueAcceptanceIsPending()
    {
        var service = Configure();
        ConfigureInbox();
        var page = Render<Inbox>();
        await page.FindAll(".message-row")[0].ClickAsync(new());
        await Button(page, "Reply").ClickAsync(new());
        service.PendingQueue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var send = Button(page, "Send").ClickAsync(new());
        page.WaitForAssertion(() => Assert.Equal(1, service.QueueCalls));
        await page.FindAll(".message-row")[1].ClickAsync(new());
        Assert.Contains("Message 2", page.Find(".reader-heading").TextContent);
        Assert.False(page.Find("button[aria-label='Delete this email']").HasAttribute("disabled"));
        service.PendingQueue.SetResult(new(Guid.NewGuid(), service.Draft.Id, service.Draft.Subject, OutgoingState.Queued, DateTimeOffset.UtcNow, null, null, null, false));
        await send;
        Assert.Contains("Queued for sending", page.Markup);
    }

    [Fact]
    public async Task InternalNavigationSavesDirtyDraftAndBlocksLeavingIfPersistenceFails()
    {
        var service = Configure();
        service.SaveFailure = new OutgoingMailException("Draft save failed. Your local text has not been saved.");
        var composer = Render<MessageComposer>(parameters => parameters.Add(component => component.Draft, service.Draft));
        await composer.Find("textarea[aria-label='Message']").InputAsync(new() { Value = "Unsaved before navigation" });
        var navigation = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        await composer.InvokeAsync(() => navigation.NavigateTo("/rules"));
        composer.WaitForAssertion(() => Assert.Contains("has not been saved", composer.Find("[role='alert']").TextContent));
        Assert.Equal("http://localhost/", navigation.Uri);
        Assert.Equal("Unsaved before navigation", service.LastSave!.Body);
    }

    [Fact]
    public async Task SendingSettingsNeverLoadSavedPasswordAndNewAccountsStartDisabled()
    {
        var service = Configure();
        Services.AddScoped<IMailboxConfigurationService>(_ => new MailboxSettingsFake());
        var page = Render<SendingAccounts>();
        await page.Find("form").SubmitAsync();
        Assert.NotNull(service.LastAccountSave);
        Assert.False(service.LastAccountSave.Enabled);
        await Button(page, "Edit Personal").ClickAsync(new());
        Assert.Equal("password", page.Find("input[aria-label='SMTP password']").GetAttribute("type"));
        Assert.True(string.IsNullOrEmpty(page.Find("input[aria-label='SMTP password']").GetAttribute("value")));
        await page.Find("form").SubmitAsync();
        Assert.Null(service.LastAccountSave!.Password);
        Assert.Contains("No test email was sent", page.Markup);
    }

    [Fact]
    public async Task EditedAccountConnectionSecuritySelectionRemainsVisibleAndPersists()
    {
        var service = Configure();
        Services.AddScoped<IMailboxConfigurationService>(_ => new MailboxSettingsFake());
        var page = Render<SendingAccounts>();

        await Button(page, "Edit Personal").ClickAsync(new());
        var security = () => (AngleSharp.Html.Dom.IHtmlSelectElement)page.Find("select[aria-label='Connection security']");
        Assert.Single(page.FindAll("option[value='starttls']"));
        Assert.Single(page.FindAll("option[value='implicit-tls']"));
        await page.Find("form").SubmitAsync();
        Assert.True(service.LastAccountSave!.UseStartTls);

        await security().ChangeAsync(new() { Value = "implicit-tls" });
        page.Render();

        await page.Find("form").SubmitAsync();
        Assert.False(service.LastAccountSave!.UseStartTls);
        Assert.False(service.Account.UseStartTls);

        await Button(page, "Edit Personal").ClickAsync(new());
        await page.Find("form").SubmitAsync();
        Assert.False(service.LastAccountSave!.UseStartTls);
    }

    [Fact]
    public void SentCopyRetryIsLabeledAsArchiveOnlyRatherThanSendingAgain()
    {
        var service = Configure();
        service.Outbox = [new(Guid.NewGuid(), service.Draft.Id, "Delivered", OutgoingState.Sent, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, "Failed", true)];
        var page = Render<Outbox>();
        Assert.NotNull(Button(page, "Retry Sent copy"));
        Assert.DoesNotContain(page.FindAll("button"), button => button.TextContent.Trim() == "Retry sending");
    }

    [Fact]
    public void UncertainSentCopyExplainsArchiveUncertaintyWithoutOfferingResend()
    {
        var service = Configure();
        service.Outbox = [new(Guid.NewGuid(), service.Draft.Id, "Delivered", OutgoingState.Sent, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, "OutcomeUnknown", false)];
        var page = Render<Outbox>();
        Assert.Contains("Check the Sent folder", page.Markup);
        Assert.Contains("email itself has already been sent", page.Markup);
        Assert.DoesNotContain(page.FindAll("button"), button => button.TextContent.Contains("Retry", StringComparison.Ordinal));
    }

    private sealed class MailboxSettingsFake : IMailboxConfigurationService
    {
        public Task<IReadOnlyList<SourceMailboxSummary>> ListSourcesAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SourceMailboxSummary>>([]);
        public Task<DestinationMailboxSummary?> GetDestinationAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default) => Task.FromResult<DestinationMailboxSummary?>(null);
        public Task<MailboxOperationResult> SaveSourceAsync(ClaimsPrincipal actor, Guid? id, SourceMailboxInput input, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailboxOperationResult> SaveSourceFoldersAsync(ClaimsPrincipal actor, Guid id, IReadOnlyList<string>? selectedFolders, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailboxOperationResult> SetSourceEnabledAsync(ClaimsPrincipal actor, Guid id, bool enabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailboxOperationResult> TestSourceAsync(ClaimsPrincipal actor, Guid id, bool discoverFolders, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailboxOperationResult> SaveDestinationAsync(ClaimsPrincipal actor, DestinationMailboxInput input, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task IdempotentSendReplayReportsAlreadySentInsteadOfClaimingASecondQueue()
    {
        var service = Configure();
        service.QueueResultState = OutgoingState.Sent;
        var composer = Render<MessageComposer>(parameters => parameters.Add(component => component.Draft, service.Draft));
        await Button(composer, "Send").ClickAsync(new());
        Assert.Contains("The sending server accepted your message", composer.Markup);
        Assert.DoesNotContain("Queued for sending", composer.Markup);
    }

    [Fact]
    public void AlreadyQueuedDraftCannotBeEditedOrSentAgainFromReopenedView()
    {
        var service = Configure();
        service.Draft = service.Draft with { OutboxId = Guid.NewGuid() };
        var composer = Render<MessageComposer>(parameters => parameters.Add(component => component.Draft, service.Draft));
        Assert.Contains("already been queued", composer.Markup);
        Assert.Empty(composer.FindAll("textarea"));
        Assert.DoesNotContain(composer.FindAll("button"), button => button.TextContent.Trim() == "Send");
    }

    private FakeOutgoing Configure()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        AddAuthorization().SetAuthorized("owner");
        var service = new FakeOutgoing();
        Services.AddSingleton<IOutgoingMailService>(service);
        return service;
    }
    private void ConfigureInbox()
    {
        Services.AddSingleton(new NavigationCountState());
        Services.AddScoped<IInboxReaderService>(_ => new InboxFake());
        Services.AddSingleton<IInboxRefreshService>(new InboxFake());
        Services.AddSingleton<IInboxDeletionQueue>(new InboxFake());
    }
    private static AngleSharp.Dom.IElement Button<T>(IRenderedComponent<T> component, string text) where T : Microsoft.AspNetCore.Components.IComponent =>
        component.FindAll("button").Single(button => button.TextContent.Trim() == text);

    private sealed class InboxFake : IInboxReaderService, IInboxRefreshService, IInboxDeletionQueue
    {
        public static readonly Guid Destination = Guid.NewGuid();
        public Task<InboxLoadResult<int>> CountAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default) => Task.FromResult(new InboxLoadResult<int>(true, 2));
        public Task<InboxLoadResult<IReadOnlyList<InboxMessageSummary>>> ListAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default) => Task.FromResult(new InboxLoadResult<IReadOnlyList<InboxMessageSummary>>(true, [new(1, 42, "Sender", "Message 1", DateTimeOffset.UtcNow), new(2, 42, "Other", "Message 2", DateTimeOffset.UtcNow)]));
        public Task<InboxLoadResult<InboxMessageContent>> ReadAsync(ClaimsPrincipal user, uint uid, uint uidValidity, CancellationToken cancellationToken = default) => Task.FromResult(new InboxLoadResult<InboxMessageContent>(true, new(uid, "Sender", "Owner", "Message " + uid, DateTimeOffset.UtcNow, "<p>message</p>", "<p>message</p>", false, [], Destination, "INBOX", "identity")));
        public Task<InboxRefreshRequest> RequestAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default) => Task.FromResult(new InboxRefreshRequest(true, "Queued", DateTimeOffset.UtcNow, 0));
        public Task<InboxRefreshStatus> GetStatusAsync(ClaimsPrincipal user, DateTimeOffset requestedUtc, CancellationToken cancellationToken = default) => Task.FromResult(new InboxRefreshStatus(0, 0, 0, 0, 0, null));
        public Task<MailboxOperationResult> QueueAsync(ClaimsPrincipal user, uint uid, uint uidValidity, CancellationToken cancellationToken = default) => Task.FromResult(new MailboxOperationResult(true, "Queued"));
    }

    internal sealed class FakeOutgoing : IOutgoingMailService
    {
        public SendingAccountView Account = new(Guid.NewGuid(), null, "Personal", "owner@example.test", "smtp.example.test", 587, true, true, "owner", true, true, SentCopyPolicy.ProviderSaves, "Sent");
        public MessageDraftView Draft;
        public SaveDraftRequest? LastSave;
        public SaveSendingAccountRequest? LastAccountSave;
        public ReplySource? ReplySource;
        public bool ReplyAll;
        public int QueueCalls, DiscardCalls;
        public OutgoingState QueueResultState = OutgoingState.Queued;
        public long QueuedRevision;
        public Exception? SaveFailure, QueueFailure;
        public TaskCompletionSource<MessageDraftView>? PendingSave;
        public TaskCompletionSource SaveStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<OutboxMessageView>? PendingQueue;
        public IReadOnlyList<OutboxMessageView> Outbox = [];
        public FakeOutgoing() { Draft = new(Guid.NewGuid(), 1, Account.Id, "sender@example.test", "", "", "Re: Original", "\n\n> Original body", [], DateTimeOffset.UtcNow, null); }
        public Task<IReadOnlyList<SendingAccountView>> ListSendingAccountsAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SendingAccountView>>([Account]);
        public Task<SendingAccountView> SaveSendingAccountAsync(ClaimsPrincipal user, SaveSendingAccountRequest request, CancellationToken cancellationToken = default)
        {
            LastAccountSave = request;
            Account = Account with { UseStartTls = request.UseStartTls };
            return Task.FromResult(Account);
        }
        public Task<MessageDraftView> CreateReplyAsync(ClaimsPrincipal user, ReplySource source, bool replyAll, CancellationToken cancellationToken = default) { ReplySource = source; ReplyAll = replyAll; return Task.FromResult(Draft); }
        public Task<MessageDraftView> CreateDraftAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default) => Task.FromResult(Draft);
        public Task<IReadOnlyList<MessageDraftView>> ListDraftsAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MessageDraftView>>([Draft]);
        public Task<MessageDraftView> GetDraftAsync(ClaimsPrincipal user, Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Draft);
        public async Task<MessageDraftView> SaveDraftAsync(ClaimsPrincipal user, SaveDraftRequest request, CancellationToken cancellationToken = default)
        {
            LastSave = request;
            SaveStarted.TrySetResult();
            if (SaveFailure is not null) throw SaveFailure;
            if (PendingSave is not null) return Draft = await PendingSave.Task;
            return Draft = Draft with { Revision = request.Revision + 1, SendingAccountId = request.SendingAccountId, To = request.To, Cc = request.Cc, Bcc = request.Bcc, Subject = request.Subject, Body = request.Body };
        }
        public Task<MessageDraftView> AddAttachmentAsync(ClaimsPrincipal user, Guid draftId, long revision, string fileName, string contentType, Stream content, CancellationToken cancellationToken = default) => Task.FromResult(Draft);
        public Task<MessageDraftView> RemoveAttachmentAsync(ClaimsPrincipal user, Guid draftId, long revision, Guid attachmentId, CancellationToken cancellationToken = default) => Task.FromResult(Draft);
        public Task DiscardDraftAsync(ClaimsPrincipal user, Guid id, long revision, CancellationToken cancellationToken = default) { DiscardCalls++; return Task.CompletedTask; }
        public Task<OutboxMessageView> QueueAsync(ClaimsPrincipal user, Guid draftId, long revision, CancellationToken cancellationToken = default)
        {
            QueueCalls++; QueuedRevision = revision;
            if (QueueFailure is not null) throw QueueFailure;
            return PendingQueue?.Task ?? Task.FromResult(new OutboxMessageView(Guid.NewGuid(), draftId, Draft.Subject, QueueResultState, DateTimeOffset.UtcNow, null, null, null, false));
        }
        public Task<IReadOnlyList<OutboxMessageView>> ListOutboxAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default) => Task.FromResult(Outbox);
        public Task<OutboxMessageView> RetryAsync(ClaimsPrincipal user, Guid outboxId, CancellationToken cancellationToken = default) => Task.FromResult(Outbox.Single(item => item.Id == outboxId));
    }
}
