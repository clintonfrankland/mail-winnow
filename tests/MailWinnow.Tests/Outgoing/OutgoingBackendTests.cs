using System.Security.Claims;
using MailWinnow.Infrastructure.Mailboxes;
using MailWinnow.Infrastructure.Outgoing;
using MailWinnow.Infrastructure.Persistence;
using MailWinnow.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MimeKit;

namespace MailWinnow.Tests.Outgoing;

public sealed class OutgoingBackendTests
{
    [Fact]
    public async Task AccountSettingsAreOwnedOptInAndPasswordIsProtectedNeverReturned()
    {
        await using var fixture = await Fixture.CreateAsync();
        var account = await fixture.AccountAsync();
        Assert.True(account.HasPassword);
        Assert.Single(await fixture.Service.ListSendingAccountsAsync(User("owner")));
        Assert.Empty(await fixture.Service.ListSendingAccountsAsync(User("other")));
        using var scope = fixture.Provider.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>().SendingAccounts.SingleAsync();
        Assert.DoesNotContain("secret", stored.ProtectedPassword!);
        await Assert.ThrowsAsync<OutgoingMailException>(() => fixture.Service.SaveSendingAccountAsync(User("other"), Request(account.Id)));
        await Assert.ThrowsAsync<OutgoingMailException>(() => fixture.Service.SaveSendingAccountAsync(User("owner"), Request() with { SourceMailboxId = Guid.NewGuid() }));
    }
    [Fact]
    public async Task DraftRevisionAndOwnerAreEnforcedAndPayloadsProtected()
    {
        await using var fixture = await Fixture.CreateAsync(); var draft = await fixture.Service.CreateDraftAsync(User("owner"));
        var saved = await fixture.Service.SaveDraftAsync(User("owner"), Save(draft) with { Body = "private content", Subject = "private subject" });
        Assert.Equal(2, saved.Revision);
        await Assert.ThrowsAsync<OutgoingMailException>(() => fixture.Service.SaveDraftAsync(User("owner"), Save(draft)));
        await Assert.ThrowsAsync<OutgoingMailException>(() => fixture.Service.GetDraftAsync(User("other"), saved.Id));
        using var scope = fixture.Provider.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
        var raw = await db.MessageDrafts.SingleAsync(); Assert.DoesNotContain("private", raw.ProtectedContent);
        var protection = fixture.Provider.GetRequiredService<OutgoingPayloadProtection>();
        Assert.Throws<System.Security.Cryptography.CryptographicException>(() => protection.Unprotect<string>("other", "draft", raw.Id, raw.ProtectedContent));
    }
    [Fact]
    public async Task AttachmentsAreProtectedSanitizedBoundedAndVersioned()
    {
        await using var fixture = await Fixture.CreateAsync(); var draft = await fixture.Service.CreateDraftAsync(User("owner"));
        var attached = await fixture.Service.AddAttachmentAsync(User("owner"), draft.Id, draft.Revision, "../../report\r\n.txt", "text/plain", new MemoryStream("private-file"u8.ToArray()));
        Assert.Equal("report.txt", Assert.Single(attached.Attachments).FileName);
        await Assert.ThrowsAsync<OutgoingMailException>(() => fixture.Service.RemoveAttachmentAsync(User("owner"), draft.Id, draft.Revision, attached.Attachments[0].Id));
        using var scope = fixture.Provider.CreateScope();
        var raw = await scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>().DraftAttachments.SingleAsync();
        Assert.DoesNotContain("private-file", System.Text.Encoding.UTF8.GetString(raw.ProtectedContent));
        var removed = await fixture.Service.RemoveAttachmentAsync(User("owner"), draft.Id, attached.Revision, attached.Attachments[0].Id);
        Assert.Empty(removed.Attachments);
        await Assert.ThrowsAsync<OutgoingMailException>(() => fixture.Service.AddAttachmentAsync(User("owner"), draft.Id, removed.Revision, "large.bin", "application/octet-stream", new MemoryStream(new byte[OutgoingMessagePolicies.MaxAttachmentBytes + 1])));
    }
    [Fact]
    public async Task QueueIsDurableExactRevisionIdempotentAndRetainsBccEnvelope()
    {
        await using var fixture = await Fixture.CreateAsync(); var draft = await fixture.ReadyDraftAsync();
        draft = await fixture.Service.SaveDraftAsync(User("owner"), Save(draft) with { Bcc = "hidden@example.test" });
        var queued = await fixture.Service.QueueAsync(User("owner"), draft.Id, draft.Revision);
        var replay = await fixture.Service.QueueAsync(User("owner"), draft.Id, draft.Revision);
        Assert.Equal(queued.Id, replay.Id); Assert.Equal(OutgoingState.Queued, queued.State); Assert.Equal(0, fixture.Transport.Calls);
        Assert.Empty(await fixture.Service.ListDraftsAsync(User("owner")));
        await Assert.ThrowsAsync<OutgoingMailException>(() => fixture.Service.SaveDraftAsync(User("owner"), Save(draft)));
        using var scope = fixture.Provider.CreateScope(); var raw = await scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>().OutgoingMessages.SingleAsync();
        using var bytes = new MemoryStream(fixture.Provider.GetRequiredService<OutgoingPayloadProtection>().UnprotectBytes("owner", "mime", raw.Id, raw.ProtectedMime));
        using var mime = await MimeMessage.LoadAsync(bytes);
        Assert.Equal("hidden@example.test", Assert.Single(mime.Bcc.Mailboxes).Address);
        Assert.False(string.IsNullOrEmpty(mime.MessageId));
    }
    [Fact]
    public async Task WorkerSubmissionIsSeparateAndUnknownOutcomesNeverRetry()
    {
        await using var fixture = await Fixture.CreateAsync(); var draft = await fixture.ReadyDraftAsync();
        var queued = await fixture.Service.QueueAsync(User("owner"), draft.Id, draft.Revision);
        fixture.Transport.Result = new(SubmissionOutcome.OutcomeUnknown, "SubmissionOutcomeUnknown");
        Assert.True(await fixture.Processor.ProcessNextAsync());
        Assert.Equal(OutgoingState.OutcomeUnknown, Assert.Single(await fixture.Service.ListOutboxAsync(User("owner"))).State);
        await Assert.ThrowsAsync<OutgoingMailException>(() => fixture.Service.RetryAsync(User("owner"), queued.Id));
        Assert.False(await fixture.Processor.ProcessNextAsync()); Assert.Equal(1, fixture.Transport.Calls);
    }
    [Fact]
    public async Task ExpiredSendingLeaseBecomesUnknownWithoutTransportCall()
    {
        await using var fixture = await Fixture.CreateAsync(); var draft = await fixture.ReadyDraftAsync();
        await fixture.Service.QueueAsync(User("owner"), draft.Id, draft.Revision);
        using (var scope = fixture.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>(); var raw = await db.OutgoingMessages.SingleAsync();
            raw.State = OutgoingState.Sending; raw.LeaseToken = Guid.NewGuid(); raw.LeaseExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1); await db.SaveChangesAsync();
        }
        Assert.False(await fixture.Processor.ProcessNextAsync());
        Assert.Equal(OutgoingState.OutcomeUnknown, Assert.Single(await fixture.Service.ListOutboxAsync(User("owner"))).State);
        Assert.Equal(0, fixture.Transport.Calls);
    }
    [Fact]
    public async Task LostOwnershipCannotCommitAnOldSendResult()
    {
        await using var fixture = await Fixture.CreateAsync(); var draft = await fixture.ReadyDraftAsync();
        await fixture.Service.QueueAsync(User("owner"), draft.Id, draft.Revision);
        fixture.Transport.BeforeResult = async () =>
        {
            using var scope = fixture.Provider.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
            await db.OutgoingMessages.ExecuteUpdateAsync(update => update.SetProperty(item => item.LeaseToken, Guid.NewGuid()).SetProperty(item => item.State, OutgoingState.OutcomeUnknown));
        };
        await fixture.Processor.ProcessNextAsync();
        Assert.Equal(OutgoingState.OutcomeUnknown, Assert.Single(await fixture.Service.ListOutboxAsync(User("owner"))).State);
    }
    [Fact]
    public async Task DefiniteFailureCanRetryUsingCorrectedSettingsButCannotChangeFrom()
    {
        await using var fixture = await Fixture.CreateAsync(); var draft = await fixture.ReadyDraftAsync();
        var queued = await fixture.Service.QueueAsync(User("owner"), draft.Id, draft.Revision);
        fixture.Transport.Result = new(SubmissionOutcome.Failed, "AuthenticationFailed"); await fixture.Processor.ProcessNextAsync();
        var account = Assert.Single(await fixture.Service.ListSendingAccountsAsync(User("owner")));
        await fixture.Service.SaveSendingAccountAsync(User("owner"), Request(account.Id) with { Password = "new-secret" });
        await fixture.Service.RetryAsync(User("owner"), queued.Id);
        fixture.Transport.Result = new(SubmissionOutcome.Accepted); await fixture.Processor.ProcessNextAsync();
        Assert.Equal("new-secret", fixture.Transport.Connection!.Password);
        Assert.Equal(OutgoingState.Sent, Assert.Single(await fixture.Service.ListOutboxAsync(User("owner"))).State);
        Assert.False(await fixture.Processor.ProcessNextAsync()); Assert.Equal(2, fixture.Transport.Calls);
    }
    [Fact]
    public async Task FailedSentCopyRetriesOnlyImapNotSmtp()
    {
        await using var fixture = await Fixture.CreateAsync(); var draft = await fixture.ReadyDraftAsync(localCopy: true);
        var queued = await fixture.Service.QueueAsync(User("owner"), draft.Id, draft.Revision);
        await fixture.Processor.ProcessNextAsync(); Assert.Equal(1, fixture.Transport.Calls);
        fixture.Imap.AppendSucceeds = false; await fixture.Processor.ProcessNextAsync();
        var failedCopy = Assert.Single(await fixture.Service.ListOutboxAsync(User("owner")));
        Assert.Equal(OutgoingState.Sent, failedCopy.State); Assert.Equal("Failed", failedCopy.SentCopyStatus); Assert.True(failedCopy.CanRetry);
        fixture.Imap.AppendSucceeds = true; await fixture.Service.RetryAsync(User("owner"), queued.Id); await fixture.Processor.ProcessNextAsync();
        Assert.Equal(1, fixture.Transport.Calls); Assert.Equal(2, fixture.Imap.AppendCalls);
        Assert.Equal("Saved", Assert.Single(await fixture.Service.ListOutboxAsync(User("owner"))).SentCopyStatus);
    }
    [Fact]
    public async Task ReplyUsesExactOwnedMimeAndDraftSurvivesOriginalRemoval()
    {
        await using var fixture = await Fixture.CreateAsync(); await fixture.AccountAsync();
        fixture.Imap.Original = new MimeMessage { MessageId = "original@example.test", Subject = "Topic", Body = new TextPart("plain") { Text = "Original text" } };
        fixture.Imap.Original.From.Add(MailboxAddress.Parse("author@example.test")); fixture.Imap.Original.ReplyTo.Add(MailboxAddress.Parse("reply@example.test"));
        fixture.Imap.Original.To.Add(MailboxAddress.Parse("owner@example.test")); fixture.Imap.Original.Cc.Add(MailboxAddress.Parse("copy@example.test")); fixture.Imap.Original.Bcc.Add(MailboxAddress.Parse("never-copy@example.test"));
        var source = await fixture.ReplySourceAsync();
        var draft = await fixture.Service.CreateReplyAsync(User("owner"), source, true);
        Assert.Equal("reply@example.test", draft.To); Assert.Equal("copy@example.test", draft.Cc); Assert.Equal("", draft.Bcc);
        Assert.Contains("> Original text", draft.Body); Assert.Equal("Re: Topic", draft.Subject); Assert.Null(draft.SendingAccountId); Assert.Empty(draft.Attachments);
        using (var scope = fixture.Provider.CreateScope()) { var db = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>(); await db.DestinationMailboxes.ExecuteDeleteAsync(); }
        Assert.Equal(draft.Body, (await fixture.Service.GetDraftAsync(User("owner"), draft.Id)).Body);
        await Assert.ThrowsAsync<OutgoingMailException>(() => fixture.Service.CreateReplyAsync(User("owner"), source, false));
    }
    [Fact]
    public async Task CrossOwnerAndChangedMailboxIdentityCannotInitializeReply()
    {
        await using var fixture = await Fixture.CreateAsync(); var source = await fixture.ReplySourceAsync();
        await Assert.ThrowsAsync<OutgoingMailException>(() => fixture.Service.CreateReplyAsync(User("other"), source, false));
        using (var scope = fixture.Provider.CreateScope()) { var db = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>(); await db.DestinationMailboxes.ExecuteUpdateAsync(update => update.SetProperty(item => item.Username, "replacement")); }
        await Assert.ThrowsAsync<OutgoingMailException>(() => fixture.Service.CreateReplyAsync(User("owner"), source, false));
        Assert.Equal(0, fixture.Imap.FetchCalls);
    }
    [Fact]
    public void RecipientParsingRejectsHeaderInjectionAndGroups()
    {
        Assert.Throws<OutgoingMailException>(() => OutgoingMessagePolicies.ParseRecipients("valid@example.test\r\nBcc: leak@example.test"));
        Assert.Throws<OutgoingMailException>(() => OutgoingMessagePolicies.ParseRecipients("Group: a@example.test;"));
        Assert.Throws<OutgoingMailException>(() => OutgoingMessagePolicies.ParseSender("first@example.test, second@example.test", "Sender"));
    }
    [Fact]
    public async Task ListQueriesExcludeLargeDraftAndOutboxPayloads()
    {
        await using var fixture = await Fixture.CreateAsync();
        var draft = await fixture.ReadyDraftAsync();
        var queued = await fixture.Service.QueueAsync(User("owner"), draft.Id, draft.Revision);
        var editable = await fixture.Service.CreateDraftAsync(User("owner"));
        editable = await fixture.Service.SaveDraftAsync(User("owner"), Save(editable) with { Subject = "Summary title", Body = "body to exclude" });
        using (var scope = fixture.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
            await db.MessageDrafts.Where(item => item.Id == editable.Id).ExecuteUpdateAsync(update => update.SetProperty(item => item.ProtectedContent, "unreadable-large-body"));
            await db.OutgoingMessages.Where(item => item.Id == queued.Id).ExecuteUpdateAsync(update => update.SetProperty(item => item.ProtectedMime, new byte[] { 255 }).SetProperty(item => item.ProtectedSettings, "unreadable-settings"));
        }
        fixture.SqlLog.Clear();
        var drafts = await fixture.Service.ListDraftsAsync(User("owner"));
        Assert.Equal("Summary title", Assert.Single(drafts).Subject); Assert.Empty(drafts[0].Body);
        Assert.Equal(queued.Id, Assert.Single(await fixture.Service.ListOutboxAsync(User("owner"))).Id);
        Assert.NotEmpty(fixture.SqlLog);
        var sql = string.Join("\n", fixture.SqlLog);
        Assert.DoesNotContain("ProtectedContent", sql); Assert.DoesNotContain("ProtectedMime", sql); Assert.DoesNotContain("ProtectedSettings", sql);
    }
    [Fact]
    public async Task AmbiguousSentAppendNeverOffersAutomaticOrManualRetry()
    {
        await using var fixture = await Fixture.CreateAsync(); var draft = await fixture.ReadyDraftAsync(localCopy: true);
        var queued = await fixture.Service.QueueAsync(User("owner"), draft.Id, draft.Revision);
        await fixture.Processor.ProcessNextAsync(); fixture.Imap.AppendSucceeds = false; fixture.Imap.AppendFailure = ImapFailureKind.Timeout;
        await fixture.Processor.ProcessNextAsync();
        var item = Assert.Single(await fixture.Service.ListOutboxAsync(User("owner")));
        Assert.Equal(OutgoingState.Sent, item.State); Assert.Equal("OutcomeUnknown", item.SentCopyStatus); Assert.False(item.CanRetry);
        await Assert.ThrowsAsync<OutgoingMailException>(() => fixture.Service.RetryAsync(User("owner"), queued.Id));
        Assert.False(await fixture.Processor.ProcessNextAsync()); Assert.Equal(1, fixture.Transport.Calls); Assert.Equal(1, fixture.Imap.AppendCalls);
    }
    [Fact]
    public async Task OnlyProvenPreSubmissionFailureRetriesAutomaticallyAndIsBounded()
    {
        await using var fixture = await Fixture.CreateAsync(); var draft = await fixture.ReadyDraftAsync();
        await fixture.Service.QueueAsync(User("owner"), draft.Id, draft.Revision);
        fixture.Transport.Result = new(SubmissionOutcome.Failed, "ConnectionFailed", true);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await fixture.Processor.ProcessNextAsync();
            using var scope = fixture.Provider.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
            await db.OutgoingMessages.ExecuteUpdateAsync(update => update.SetProperty(item => item.NextAttemptUtc, (DateTimeOffset?)null));
        }
        Assert.Equal(OutgoingState.Failed, Assert.Single(await fixture.Service.ListOutboxAsync(User("owner"))).State);
        Assert.False(await fixture.Processor.ProcessNextAsync()); Assert.Equal(3, fixture.Transport.Calls);
    }
    [Fact]
    public async Task DisabledSendingAccountStopsQueuedMailWithoutSmtp()
    {
        await using var fixture = await Fixture.CreateAsync(); var draft = await fixture.ReadyDraftAsync();
        await fixture.Service.QueueAsync(User("owner"), draft.Id, draft.Revision);
        var account = Assert.Single(await fixture.Service.ListSendingAccountsAsync(User("owner")));
        await fixture.Service.SaveSendingAccountAsync(User("owner"), Request(account.Id) with { Enabled = false });
        await fixture.Processor.ProcessNextAsync(); Assert.Equal(0, fixture.Transport.Calls);
        Assert.Equal(OutgoingState.Failed, Assert.Single(await fixture.Service.ListOutboxAsync(User("owner"))).State);
    }
    [Fact]
    public void LargeMalformedHtmlQuotationIsBoundedAndContainsNoActiveMarkup()
    {
        using var mime = new MimeMessage { Body = new TextPart("html") { Text = new string('<', 200000) + "<script>bad()</script>" } };
        var quote = OutgoingMessagePolicies.Quote(mime);
        Assert.True(quote.Length < 100100);
    }
    [Fact]
    public async Task SuccessfulSentStorageExpiresButFailedUnknownAndRecentStay()
    {
        await using var fixture = await Fixture.CreateAsync();
        var expired = await fixture.ReadyDraftAsync();
        expired = await fixture.Service.AddAttachmentAsync(User("owner"), expired.Id, expired.Revision, "sent.txt", "text/plain", new MemoryStream("retained"u8.ToArray()));
        var sent = await fixture.Service.QueueAsync(User("owner"), expired.Id, expired.Revision); await fixture.Processor.ProcessNextAsync();
        var unknownDraft = await fixture.ReadyDraftAsync(); var unknown = await fixture.Service.QueueAsync(User("owner"), unknownDraft.Id, unknownDraft.Revision);
        fixture.Transport.Result = new(SubmissionOutcome.OutcomeUnknown, "uncertain"); await fixture.Processor.ProcessNextAsync();
        using (var scope = fixture.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
            await db.OutgoingMessages.ExecuteUpdateAsync(update => update.SetProperty(item => item.SentUtc, DateTimeOffset.UtcNow.AddDays(-31)));
        }
        var recent = await fixture.ReadyDraftAsync(); await fixture.Service.QueueAsync(User("owner"), recent.Id, recent.Revision);
        fixture.Transport.Result = new(SubmissionOutcome.Accepted); await fixture.Processor.ProcessNextAsync();
        Assert.Equal(1, await fixture.Processor.CleanupSentAsync());
        var remaining = await fixture.Service.ListOutboxAsync(User("owner")); Assert.Equal(2, remaining.Count); Assert.Contains(remaining, item => item.Id == unknown.Id);
        await Assert.ThrowsAsync<OutgoingMailException>(() => fixture.Service.GetDraftAsync(User("owner"), expired.Id));
        using var checkScope = fixture.Provider.CreateScope(); Assert.Empty(await checkScope.ServiceProvider.GetRequiredService<MailWinnowDbContext>().DraftAttachments.ToListAsync());
    }
    [Theory]
    [InlineData(OutgoingState.Sent, "Saved", true)]
    [InlineData(OutgoingState.Sent, "ProviderSaves", true)]
    [InlineData(OutgoingState.Sent, "Pending", false)]
    [InlineData(OutgoingState.Sent, "Copying", false)]
    [InlineData(OutgoingState.Sent, "Failed", false)]
    [InlineData(OutgoingState.Sent, "OutcomeUnknown", false)]
    [InlineData(OutgoingState.Sent, null, false)]
    [InlineData(OutgoingState.Failed, "Saved", false)]
    [InlineData(OutgoingState.Queued, "Saved", false)]
    [InlineData(OutgoingState.Sending, "Saved", false)]
    [InlineData(OutgoingState.OutcomeUnknown, "Saved", false)]
    public async Task CleanupOnlyDeletesUnambiguousSuccessIncludingItsFrozenAttachments(OutgoingState state, string? copyStatus, bool deleted)
    {
        await using var fixture = await Fixture.CreateAsync(); var draft = await fixture.ReadyDraftAsync();
        draft = await fixture.Service.AddAttachmentAsync(User("owner"), draft.Id, draft.Revision, "kept.txt", "text/plain", new MemoryStream([1, 2, 3]));
        await fixture.Service.QueueAsync(User("owner"), draft.Id, draft.Revision);
        using (var scope = fixture.Provider.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
            await database.OutgoingMessages.ExecuteUpdateAsync(update => update.SetProperty(item => item.State, state).SetProperty(item => item.SentCopyStatus, copyStatus).SetProperty(item => item.SentUtc, DateTimeOffset.UtcNow.AddDays(-31)));
        }
        Assert.Equal(deleted ? 1 : 0, await fixture.Processor.CleanupSentAsync());
        using var checkedScope = fixture.Provider.CreateScope(); var checkedDatabase = checkedScope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
        Assert.Equal(deleted ? 0 : 1, await checkedDatabase.OutgoingMessages.CountAsync());
        Assert.Equal(deleted ? 0 : 1, await checkedDatabase.MessageDrafts.CountAsync());
        Assert.Equal(deleted ? 0 : 1, await checkedDatabase.DraftAttachments.CountAsync());
    }
    [Fact]
    public async Task CleanupFailureRollsBackOutboxAndDraftDeletionTogether()
    {
        await using var fixture = await Fixture.CreateAsync(); var draft = await fixture.ReadyDraftAsync();
        await fixture.Service.QueueAsync(User("owner"), draft.Id, draft.Revision); await fixture.Processor.ProcessNextAsync();
        using (var scope = fixture.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
            await db.OutgoingMessages.ExecuteUpdateAsync(update => update.SetProperty(item => item.SentUtc, DateTimeOffset.UtcNow.AddDays(-31)));
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER block_cleanup BEFORE DELETE ON MessageDrafts BEGIN SELECT RAISE(ABORT, 'forced cleanup failure'); END;");
        }
        await Assert.ThrowsAsync<SqliteException>(() => fixture.Processor.CleanupSentAsync());
        Assert.Single(await fixture.Service.ListOutboxAsync(User("owner"))); Assert.Equal(draft.Id, (await fixture.Service.GetDraftAsync(User("owner"), draft.Id)).Id);
    }
    [Fact]
    public async Task EditableAttachmentQuotaDoesNotCountLockedQueuedDrafts()
    {
        await using var fixture = await Fixture.CreateAsync(); var draft = await fixture.ReadyDraftAsync();
        draft = await fixture.Service.AddAttachmentAsync(User("owner"), draft.Id, draft.Revision, "small.txt", "text/plain", new MemoryStream([1]));
        using (var scope = fixture.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
            await db.DraftAttachments.ExecuteUpdateAsync(update => update.SetProperty(item => item.Length, 200 * 1024 * 1024));
        }
        var next = await fixture.Service.CreateDraftAsync(User("owner"));
        await Assert.ThrowsAsync<OutgoingMailException>(() => fixture.Service.AddAttachmentAsync(User("owner"), next.Id, next.Revision, "next.txt", "text/plain", new MemoryStream([1])));
        using (var scope = fixture.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
            await db.MessageDrafts.Where(item => item.Id == draft.Id).ExecuteUpdateAsync(update => update.SetProperty(item => item.OutboxId, Guid.NewGuid()));
        }
        var attached = await fixture.Service.AddAttachmentAsync(User("owner"), next.Id, next.Revision, "next.txt", "text/plain", new MemoryStream([1]));
        Assert.Single(attached.Attachments);
    }
    [Fact]
    public async Task LongSubmissionRenewsItsExactOwnershipLease()
    {
        await using var fixture = await Fixture.CreateAsync(); var draft = await fixture.ReadyDraftAsync();
        await fixture.Service.QueueAsync(User("owner"), draft.Id, draft.Revision);
        fixture.Transport.BeforeResult = async () =>
        {
            DateTimeOffset? firstLease;
            using (var scope = fixture.Provider.CreateScope()) firstLease = await scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>().OutgoingMessages.Select(item => item.LeaseExpiresUtc).SingleAsync();
            await Task.Delay(TimeSpan.FromSeconds(32));
            using var check = fixture.Provider.CreateScope(); var message = await check.ServiceProvider.GetRequiredService<MailWinnowDbContext>().OutgoingMessages.SingleAsync();
            Assert.True(message.LeaseExpiresUtc > firstLease); Assert.Equal(OutgoingState.Sending, message.State);
        };
        await fixture.Processor.ProcessNextAsync(); Assert.Equal(OutgoingState.Sent, Assert.Single(await fixture.Service.ListOutboxAsync(User("owner"))).State);
    }
    private static ClaimsPrincipal User(string owner) => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, owner)], "test"));
    private static SaveSendingAccountRequest Request(Guid? id = null) => new(id, null, "Owner", "owner@example.test", "smtp.example.test", 465, false, true, "owner", "secret", true, SentCopyPolicy.ProviderSaves);
    private static SaveDraftRequest Save(MessageDraftView draft) => new(draft.Id, draft.Revision, draft.SendingAccountId, draft.To, draft.Cc, draft.Bcc, draft.Subject, draft.Body);
    private sealed class Fixture(SqliteConnection connection, ServiceProvider provider, FakeTransport transport, FakeImap imap, List<string> sqlLog) : IAsyncDisposable
    {
        public ServiceProvider Provider => provider;
        public List<string> SqlLog => sqlLog;
        public FakeTransport Transport => transport;
        public FakeImap Imap => imap;
        public IOutgoingMailService Service => provider.GetRequiredService<IOutgoingMailService>();
        public OutgoingQueueProcessor Processor => provider.GetRequiredService<OutgoingQueueProcessor>();
        public Task<SendingAccountView> AccountAsync(bool localCopy = false) => Service.SaveSendingAccountAsync(User("owner"), Request() with { SentCopyPolicy = localCopy ? SentCopyPolicy.LocalSentFolder : SentCopyPolicy.ProviderSaves });
        public async Task<MessageDraftView> ReadyDraftAsync(bool localCopy = false)
        {
            if (localCopy) await ReplySourceAsync();
            var account = await AccountAsync(localCopy); var draft = await Service.CreateDraftAsync(User("owner"));
            return await Service.SaveDraftAsync(User("owner"), Save(draft) with { SendingAccountId = account.Id, To = "recipient@example.test", Subject = "Hello", Body = "Private composed text" });
        }
        public async Task<ReplySource> ReplySourceAsync()
        {
            using var scope = provider.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
            var destination = new DestinationMailbox { OwnerUserId = "owner", Username = "owner-local", ProtectedCredential = "not-used", Folder = "INBOX" }; db.DestinationMailboxes.Add(destination); await db.SaveChangesAsync();
            return new(destination.Id, destination.Folder, InboxAttachmentService.MailboxIdentity(destination.Id, destination.Username, destination.Folder, new LocalImapOptions()), 1, 42);
        }
        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
            var transport = new FakeTransport(); var imap = new FakeImap(); var sqlLog = new List<string>();
            var services = new ServiceCollection(); services.AddDbContext<MailWinnowDbContext>(options => options.UseSqlite(connection).ReplaceService<IModelCustomizer, SqliteOutgoingModelCustomizer>().LogTo(sqlLog.Add, [RelationalEventId.CommandExecuted], LogLevel.Information));
            services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            services.AddSingleton<OutgoingPayloadProtection>(); services.AddSingleton<ICredentialProtectionService, TestCredentials>(); services.AddScoped<IOwnershipAuthorizer, OwnershipAuthorizer>();
            services.AddSingleton<IOutgoingTransport>(transport); services.AddSingleton<IImapConnectionService>(imap); services.AddSingleton<IOptions<LocalImapOptions>>(Options.Create(new LocalImapOptions()));
            services.AddSingleton<IOutgoingMailService, OutgoingMailService>(); services.AddSingleton<OutgoingQueueProcessor>();
            var provider = services.BuildServiceProvider(); using var scope = provider.CreateScope(); await scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>().Database.EnsureCreatedAsync();
            return new(connection, provider, transport, imap, sqlLog);
        }
        public async ValueTask DisposeAsync() { await provider.DisposeAsync(); await connection.DisposeAsync(); }
    }
    public sealed class SqliteOutgoingModelCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            base.Customize(modelBuilder, context);
            foreach (var entity in modelBuilder.Model.GetEntityTypes().Where(entity => entity.ClrType.Namespace == typeof(OutgoingMessage).Namespace))
                foreach (var property in entity.GetProperties().Where(property => property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?)))
                    property.SetValueConverter(new DateTimeOffsetToBinaryConverter());
        }
    }
    private sealed class TestCredentials(IDataProtectionProvider provider) : ICredentialProtectionService
    {
        public string Protect(string credential, CredentialKind kind) => provider.CreateProtector("test-credentials", kind.ToString()).Protect(credential);
        public string Unprotect(string credential, CredentialKind kind) => credential == "not-used" ? credential : provider.CreateProtector("test-credentials", kind.ToString()).Unprotect(credential);
    }
    private sealed class FakeTransport : IOutgoingTransport
    {
        public int Calls { get; private set; }
        public SendingConnection? Connection { get; private set; }
        public SubmissionResult Result { get; set; } = new(SubmissionOutcome.Accepted);
        public Func<Task>? BeforeResult { get; set; }
        public async Task<SubmissionResult> SubmitAsync(SendingConnection connection, MimeMessage message, CancellationToken cancellationToken)
        { Calls++; Connection = connection; if (BeforeResult is not null) await BeforeResult(); return Result; }
    }
    private sealed class FakeImap : IImapConnectionService
    {
        public MimeMessage Original { get; set; } = new() { Body = new TextPart("plain") { Text = "original" } };
        public bool AppendSucceeds { get; set; } = true;
        public int AppendCalls { get; private set; }
        public ImapFailureKind AppendFailure { get; set; } = ImapFailureKind.Authentication;
        public int FetchCalls { get; private set; }
        public Task<ImapOperationResult<MimeMessage>> FetchMessageAsync(ImapConnectionSettings connection, string folder, uint uid, uint? validity = null, CancellationToken token = default)
        { FetchCalls++; return Task.FromResult(ImapOperationResult<MimeMessage>.Success(Original)); }
        public Task<ImapOperationResult<uint?>> AppendMessageAsync(ImapConnectionSettings connection, string folder, MimeMessage message, CancellationToken token = default, DateTimeOffset? date = null)
        { AppendCalls++; return Task.FromResult(AppendSucceeds ? ImapOperationResult<uint?>.Success(123) : ImapOperationResult<uint?>.Failure(AppendFailure, "safe")); }
        public Task<ImapOperationResult<IReadOnlyList<ImapMessageHeader>>> FetchHeadersAsync(ImapConnectionSettings connection, string folder, IReadOnlyList<uint> uids, uint? validity = null, CancellationToken token = default) => throw new NotSupportedException();
        public Task<ImapOperationResult<ImapFolderSnapshot>> GetFolderSnapshotAsync(ImapConnectionSettings connection, string folder, CancellationToken token = default) => throw new NotSupportedException();
        public Task<ImapOperationResult<IReadOnlyList<string>>> ListFoldersAsync(ImapConnectionSettings connection, CancellationToken token = default) => throw new NotSupportedException();
        public Task<ImapOperationResult<int>> DeleteAndExpungeAsync(ImapConnectionSettings connection, string folder, IReadOnlyList<uint> uids, uint validity, CancellationToken token = default) => throw new NotSupportedException();
        public Task<ImapOperationResult<bool>> TestConnectionAsync(ImapConnectionSettings connection, CancellationToken token = default) => throw new NotSupportedException();
    }
}
