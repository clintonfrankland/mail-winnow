using MailWinnow.Core.Rules;
using MailWinnow.Infrastructure.Mailboxes;
using MailWinnow.Infrastructure.Persistence;
using MailWinnow.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimeKit;
using System.Reflection;
using System.Security.Claims;

namespace MailWinnow.Tests.Mailboxes;

public sealed class MessageDeliveryTests
{
    [Fact]
    public async Task Approved_message_is_appended_once_and_records_destination_identity()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Service.QueueApprovedAsync("owner", f.Header.Id);
        await f.Service.QueueApprovedAsync("owner", f.Header.Id);
        var delivery = Assert.Single(await f.Db.MessageDeliveries.ToListAsync());

        await f.Service.DeliverAsync(delivery.Id);
        await f.Service.DeliverAsync(delivery.Id);

        delivery = await f.Db.MessageDeliveries.SingleAsync();
        Assert.Equal(MessageDeliveryState.Delivered, delivery.State);
        Assert.Equal(77u, delivery.DestinationUid);
        Assert.Equal(42u, delivery.DestinationUidValidity);
        Assert.NotNull(delivery.DeliveredUtc);
        Assert.Null(delivery.ExpiresUtc);
        Assert.NotNull(delivery.SourceDeletedUtc);
        Assert.Equal(1, f.Imap.AppendCalls);
        Assert.Equal(1, f.Imap.DeleteCalls);
        Assert.DoesNotContain(typeof(MessageDelivery).GetProperties(), x => x.PropertyType == typeof(MimeMessage));
    }

    [Fact]
    public async Task HeaderAlreadyDeletedByBlockRuleCannotBeQueuedForDelivery()
    {
        await using var f = await Fixture.CreateAsync();
        f.Header.BlockedSourceDeletedUtc = DateTimeOffset.UtcNow;
        await f.Db.SaveChangesAsync();

        await f.Service.QueueApprovedAsync("owner", f.Header.Id);

        Assert.Empty(await f.Db.MessageDeliveries.ToListAsync());
    }

    [Fact]
    public async Task Concurrent_service_scopes_claim_a_delivery_atomically_before_appending()
    {
        await using var f = await Fixture.CreateAsync();
        f.Imap.HoldFetches = true;
        await f.Service.QueueApprovedAsync("owner", f.Header.Id);
        var deliveryId = (await f.Db.MessageDeliveries.SingleAsync()).Id;
        await using var otherDb = new MailWinnowDbContext(new DbContextOptionsBuilder<MailWinnowDbContext>().UseSqlite(f.Connection).Options);
        var otherService = f.CreateService(otherDb);

        var first = f.Service.DeliverAsync(deliveryId);
        await f.Imap.FetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await otherService.DeliverAsync(deliveryId);
        f.Imap.ReleaseFetches();
        await first;

        Assert.Equal(1, f.Imap.AppendCalls);
        Assert.Equal(MessageDeliveryState.Delivered, (await f.Db.MessageDeliveries.SingleAsync()).State);
    }

    [Fact]
    public async Task Old_stale_snapshot_cannot_reset_a_refreshed_claim_or_append_a_second_copy()
    {
        await using var f = await Fixture.CreateAsync();
        f.Imap.HoldFetches = true;
        await f.Service.QueueApprovedAsync("owner", f.Header.Id);
        var delivery = await f.Db.MessageDeliveries.SingleAsync();
        delivery.State = MessageDeliveryState.Fetching;
        delivery.FetchStartedUtc = DateTimeOffset.UtcNow.AddMinutes(-6);
        await f.Db.SaveChangesAsync();

        await using var oldSnapshotDb = new MailWinnowDbContext(new DbContextOptionsBuilder<MailWinnowDbContext>().UseSqlite(f.Connection).Options);
        var oldSnapshot = await oldSnapshotDb.MessageDeliveries.AsNoTracking().SingleAsync();
        await using var activeDb = new MailWinnowDbContext(new DbContextOptionsBuilder<MailWinnowDbContext>().UseSqlite(f.Connection).Options);
        var activeService = f.CreateService(activeDb);

        var activeDelivery = activeService.DeliverAsync(delivery.Id);
        await f.Imap.FetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var delayedService = f.CreateService(oldSnapshotDb);
        var claim = typeof(MessageDeliveryService).GetMethod("TryClaimAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var delayedClaim = (Task<bool>)claim.Invoke(delayedService, [oldSnapshot, CancellationToken.None])!;
        Assert.False(await delayedClaim);

        f.Imap.ReleaseFetches();
        await activeDelivery;

        f.Db.ChangeTracker.Clear();
        Assert.Equal(1, f.Imap.AppendCalls);
        Assert.Equal(MessageDeliveryState.Delivered, (await f.Db.MessageDeliveries.SingleAsync()).State);
    }

    [Fact]
    public async Task Administrative_retry_cannot_requeue_an_active_claim_or_append_a_second_copy()
    {
        await using var f = await Fixture.CreateAsync();
        f.Imap.HoldFetches = true;
        await f.Service.QueueApprovedAsync("owner", f.Header.Id);
        var deliveryId = (await f.Db.MessageDeliveries.SingleAsync()).Id;
        await using var adminDb = new MailWinnowDbContext(new DbContextOptionsBuilder<MailWinnowDbContext>().UseSqlite(f.Connection).Options);
        var adminService = f.CreateService(adminDb);

        var activeDelivery = f.Service.DeliverAsync(deliveryId);
        await f.Imap.FetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var retry = await adminService.RetryAsync(Principal("owner"), deliveryId);
        Assert.False(retry.Succeeded);

        f.Imap.ReleaseFetches();
        await activeDelivery;

        f.Db.ChangeTracker.Clear();
        Assert.Equal(1, f.Imap.AppendCalls);
        Assert.Equal(MessageDeliveryState.Delivered, (await f.Db.MessageDeliveries.SingleAsync()).State);
    }

    [Fact]
    public async Task Source_fetch_failure_is_recorded_separately_and_can_be_retried()
    {
        await using var f = await Fixture.CreateAsync();
        f.Imap.FetchFailure = true;
        await f.Service.QueueApprovedAsync("owner", f.Header.Id);
        var delivery = await f.Db.MessageDeliveries.SingleAsync();
        await f.Service.DeliverAsync(delivery.Id);

        delivery = await f.Db.MessageDeliveries.SingleAsync();
        Assert.Equal(MessageDeliveryState.Failed, delivery.State);
        Assert.Equal("Fetch", delivery.LastFailureStage);
        Assert.Equal(0, f.Imap.AppendCalls);
    }

    [Fact]
    public async Task Source_delete_failure_after_append_retries_without_appending_a_duplicate()
    {
        await using var f = await Fixture.CreateAsync();
        f.Imap.DeleteFailure = true;
        await f.Service.QueueApprovedAsync("owner", f.Header.Id);
        var delivery = await f.Db.MessageDeliveries.SingleAsync();

        await f.Service.DeliverAsync(delivery.Id);

        delivery = await f.Db.MessageDeliveries.SingleAsync();
        Assert.Equal(MessageDeliveryState.Failed, delivery.State);
        Assert.Equal("Source deletion", delivery.LastFailureStage);
        Assert.Equal(1, f.Imap.AppendCalls);
        Assert.Null(delivery.SourceDeletedUtc);

        f.Imap.DeleteFailure = false;
        Assert.True((await f.Service.RetryAsync(Principal("owner"), delivery.Id)).Succeeded);
        await f.Service.DeliverAsync(delivery.Id);

        delivery = await f.Db.MessageDeliveries.SingleAsync();
        Assert.Equal(MessageDeliveryState.Delivered, delivery.State);
        Assert.Equal(1, f.Imap.AppendCalls);
        Assert.Equal(2, f.Imap.DeleteCalls);
        Assert.NotNull(delivery.SourceDeletedUtc);
    }

    [Fact]
    public async Task Recovered_append_receipt_completes_without_a_second_append()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Service.QueueApprovedAsync("owner", f.Header.Id);
        var delivery = await f.Db.MessageDeliveries.SingleAsync();
        delivery.State = MessageDeliveryState.Delivering;
        delivery.DeliveryStartedUtc = DateTimeOffset.UtcNow.AddMinutes(-6);
        delivery.DestinationUid = 77;
        delivery.DestinationUidValidity = 42;
        await f.Db.SaveChangesAsync();

        await f.Service.DeliverAsync(delivery.Id);

        Assert.Equal(MessageDeliveryState.Delivered, (await f.Db.MessageDeliveries.SingleAsync()).State);
        Assert.Equal(0, f.Imap.AppendCalls);
    }

    [Fact]
    public async Task Crash_after_append_before_receipt_is_reconciled_without_a_duplicate()
    {
        await using var f = await Fixture.CreateAsync();
        f.Imap.DestinationUids = [77];
        f.Imap.DestinationHeaders = [f.Imap.Header(77, f.Header.MessageId!)];
        await f.Service.QueueApprovedAsync("owner", f.Header.Id);
        var delivery = await f.Db.MessageDeliveries.SingleAsync();
        delivery.State = MessageDeliveryState.Delivering;
        delivery.DeliveryStartedUtc = DateTimeOffset.UtcNow.AddMinutes(-6);
        delivery.DestinationUidValidity = 42;
        delivery.DestinationUidFloor = 76;
        await f.Db.SaveChangesAsync();

        await f.Service.DeliverAsync(delivery.Id);

        delivery = await f.Db.MessageDeliveries.SingleAsync();
        Assert.Equal(MessageDeliveryState.Delivered, delivery.State);
        Assert.Equal(77u, delivery.DestinationUid);
        Assert.Equal(0, f.Imap.AppendCalls);
    }

    [Fact]
    public async Task Recovery_ignores_unrelated_post_floor_messages_and_matches_the_original_message_id()
    {
        await using var f = await Fixture.CreateAsync();
        f.Imap.DestinationUids = [77, 78];
        f.Imap.DestinationHeaders = [f.Imap.Header(77, "<unrelated@example.test>"), f.Imap.Header(78, f.Header.MessageId!)];
        await f.Service.QueueApprovedAsync("owner", f.Header.Id);
        var delivery = await f.Db.MessageDeliveries.SingleAsync();
        delivery.State = MessageDeliveryState.Delivering;
        delivery.DeliveryStartedUtc = DateTimeOffset.UtcNow.AddMinutes(-6);
        delivery.DestinationUidValidity = 42;
        delivery.DestinationUidFloor = 76;
        await f.Db.SaveChangesAsync();

        await f.Service.DeliverAsync(delivery.Id);

        delivery = await f.Db.MessageDeliveries.SingleAsync();
        Assert.Equal(MessageDeliveryState.Delivered, delivery.State);
        Assert.Equal(78u, delivery.DestinationUid);
        Assert.Equal(0, f.Imap.AppendCalls);
    }

    [Fact]
    public async Task Recovery_does_not_claim_an_unrelated_post_floor_message()
    {
        await using var f = await Fixture.CreateAsync();
        f.Imap.DestinationUids = [77];
        f.Imap.DestinationHeaders = [f.Imap.Header(77, "<unrelated@example.test>")];
        await f.Service.QueueApprovedAsync("owner", f.Header.Id);
        var delivery = await f.Db.MessageDeliveries.SingleAsync();
        delivery.State = MessageDeliveryState.Delivering;
        delivery.DeliveryStartedUtc = DateTimeOffset.UtcNow.AddMinutes(-6);
        delivery.DestinationUidValidity = 42;
        delivery.DestinationUidFloor = 76;
        await f.Db.SaveChangesAsync();

        await f.Service.DeliverAsync(delivery.Id);

        delivery = await f.Db.MessageDeliveries.SingleAsync();
        Assert.Equal(MessageDeliveryState.Failed, delivery.State);
        Assert.Null(delivery.DestinationUid);
        Assert.Equal(0, f.Imap.AppendCalls);
    }

    [Fact]
    public async Task Ambiguous_accepted_append_with_no_visible_match_never_retries_another_append()
    {
        await using var f = await Fixture.CreateAsync();
        f.Imap.AppendResponseLost = true;
        await f.Service.QueueApprovedAsync("owner", f.Header.Id);
        var delivery = await f.Db.MessageDeliveries.SingleAsync();

        await f.Service.DeliverAsync(delivery.Id);
        Assert.Equal(MessageDeliveryState.Failed, (await f.Db.MessageDeliveries.SingleAsync()).State);
        Assert.Equal(1, f.Imap.AppendCalls);

        delivery.State = MessageDeliveryState.RetryPending;
        await f.Db.SaveChangesAsync();
        await f.Service.DeliverAsync(delivery.Id);

        Assert.Equal(MessageDeliveryState.Failed, (await f.Db.MessageDeliveries.SingleAsync()).State);
        Assert.Equal(1, f.Imap.AppendCalls);
    }

    [Fact]
    public async Task Stale_delivery_before_append_is_recovered_and_transferred_once()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Service.QueueApprovedAsync("owner", f.Header.Id);
        var delivery = await f.Db.MessageDeliveries.SingleAsync();
        delivery.State = MessageDeliveryState.Delivering;
        delivery.DeliveryStartedUtc = DateTimeOffset.UtcNow.AddMinutes(-6);
        await f.Db.SaveChangesAsync();

        await f.Service.DeliverAsync(delivery.Id);

        Assert.Equal(MessageDeliveryState.Delivered, (await f.Db.MessageDeliveries.SingleAsync()).State);
        Assert.Equal(1, f.Imap.AppendCalls);
    }

    [Fact]
    public async Task Reusable_allow_rule_queues_delivery_with_the_applied_rules_retention()
    {
        await using var f = await Fixture.CreateAsync();
        f.Header.EvaluationOutcome = RuleOutcome.Pending;
        var rule = new MailWinnow.Infrastructure.Rules.MailRule
        {
            OwnerUserId = "owner", Action = RuleAction.PermanentlyAllow, Scope = RuleScope.User,
            MatchType = RuleMatchType.ExactSender, MatchValue = "allowed@example.test", DeliveredMessageRetentionDays = 7
        };
        f.Header.From = "allowed@example.test";
        f.Db.MailRules.Add(rule);
        await f.Db.SaveChangesAsync();

        await new MailWinnow.Infrastructure.Rules.RuleEvaluationService(f.Db, f.Service).EvaluateAsync("owner", f.Header.Id, DateTimeOffset.UtcNow);
        var delivery = await f.Db.MessageDeliveries.SingleAsync();

        Assert.Equal(rule.Id, delivery.ApprovalRuleId);
        await f.Service.DeliverAsync(delivery.Id);
        Assert.Equal(7, ((await f.Db.MessageDeliveries.SingleAsync()).ExpiresUtc!.Value - (await f.Db.MessageDeliveries.SingleAsync()).DeliveredUtc!.Value).TotalDays);
    }

    [Fact]
    public async Task Expired_delivery_is_deleted_once_using_its_recorded_destination_identity()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Service.QueueApprovedAsync("owner", f.Header.Id);
        var delivery = await f.Db.MessageDeliveries.SingleAsync();
        await f.Service.DeliverAsync(delivery.Id);
        delivery = await f.Db.MessageDeliveries.SingleAsync();
        delivery.ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        await f.Db.SaveChangesAsync();

        await f.Service.CleanupExpiredAsync(delivery.Id);
        await f.Service.CleanupExpiredAsync(delivery.Id);

        delivery = await f.Db.MessageDeliveries.SingleAsync();
        Assert.Equal(MessageDeliveryState.Deleted, delivery.State);
        Assert.NotNull(delivery.DeletedUtc);
        Assert.Equal(2, f.Imap.DeleteCalls);
        Assert.Equal(77u, f.Imap.DeletedUid);
        Assert.Equal(42u, f.Imap.DeletedUidValidity);
        Assert.Equal("INBOX", f.Imap.DeletedFolder);
    }

    private sealed class Fixture(SqliteConnection connection, MailWinnowDbContext db, SourceMessageHeader header, FakeImap imap, MessageDeliveryService service) : IAsyncDisposable
    {
        public SqliteConnection Connection { get; } = connection; public MailWinnowDbContext Db { get; } = db; public SourceMessageHeader Header { get; } = header; public FakeImap Imap { get; } = imap; public MessageDeliveryService Service { get; } = service;
        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
            var db = new MailWinnowDbContext(new DbContextOptionsBuilder<MailWinnowDbContext>().UseSqlite(connection).Options); await db.Database.EnsureCreatedAsync();
            var source = new SourceMailbox { OwnerUserId = "owner", DisplayName = "source", Host = "source.test", Port = 993, UseSsl = true, Username = "source", ProtectedCredential = "source" };
            var destination = new DestinationMailbox { OwnerUserId = "owner", Username = "destination", Folder = "INBOX", ProtectedCredential = "destination" };
            var header = new SourceMessageHeader { SourceMailboxId = source.Id, FolderName = "INBOX", UidValidity = 1, Uid = 10, MessageId = "<original@example.test>", ReceivedUtc = DateTimeOffset.UtcNow, EvaluationOutcome = RuleOutcome.Allow };
            db.AddRange(source, destination, header); await db.SaveChangesAsync();
            header.EvaluationOutcome = RuleOutcome.Allow; await db.SaveChangesAsync();
            var imap = new FakeImap();
            var service = new MessageDeliveryService(db, new Protector(), imap, Options.Create(new LocalImapOptions { Host = "local.test", Port = 993, UseSsl = true }), new OwnershipAuthorizer());
            return new(connection, db, header, imap, service);
        }
        public MessageDeliveryService CreateService(MailWinnowDbContext context) => new(context, new Protector(), Imap, Options.Create(new LocalImapOptions { Host = "local.test", Port = 993, UseSsl = true }), new OwnershipAuthorizer());
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await Connection.DisposeAsync(); }
    }
    private sealed class Protector : ICredentialProtectionService { public string Protect(string value, CredentialKind kind) => value; public string Unprotect(string value, CredentialKind kind) => value; }
    private static ClaimsPrincipal Principal(string userId) => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "Test"));
    private sealed class FakeImap : IImapConnectionService
    {
        public bool FetchFailure { get; set; } public bool AppendResponseLost { get; set; } public bool DeleteFailure { get; set; } public bool HoldFetches { get; set; } public int AppendCalls { get; private set; } public int DeleteCalls { get; private set; } public uint DeletedUid { get; private set; } public uint DeletedUidValidity { get; private set; } public string? DeletedFolder { get; private set; }
        public TaskCompletionSource FetchStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource fetchRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<uint> DestinationUids { get; set; } = [];
        public IReadOnlyList<ImapMessageHeader> DestinationHeaders { get; set; } = [];
        public ImapMessageHeader Header(uint uid, string messageId) => new(uid, messageId, null, null, null, null, null, null, null, null);
        public async Task<ImapOperationResult<MimeMessage>> FetchMessageAsync(ImapConnectionSettings c, string f, uint u, uint? v = null, CancellationToken t = default) { FetchStarted.TrySetResult(); if (HoldFetches) await fetchRelease.Task.WaitAsync(t); return FetchFailure ? ImapOperationResult<MimeMessage>.Failure(ImapFailureKind.Transient, "safe failure") : ImapOperationResult<MimeMessage>.Success(new MimeMessage { Subject = "original", MessageId = "<original@example.test>" }); }
        public void ReleaseFetches() => fetchRelease.TrySetResult();
        public Task<ImapOperationResult<uint?>> AppendMessageAsync(ImapConnectionSettings c, string f, MimeMessage m, CancellationToken t = default, DateTimeOffset? receivedUtc = null) { AppendCalls++; return Task.FromResult(AppendResponseLost ? ImapOperationResult<uint?>.Failure(ImapFailureKind.Transient, "response lost") : ImapOperationResult<uint?>.Success(77)); }
        public Task<ImapOperationResult<ImapFolderSnapshot>> GetFolderSnapshotAsync(ImapConnectionSettings c, string f, CancellationToken t = default) => Task.FromResult(ImapOperationResult<ImapFolderSnapshot>.Success(new(42, DestinationUids)));
        public Task<ImapOperationResult<IReadOnlyList<string>>> ListFoldersAsync(ImapConnectionSettings c, CancellationToken t = default) => throw new NotSupportedException();
        public Task<ImapOperationResult<IReadOnlyList<ImapMessageHeader>>> FetchHeadersAsync(ImapConnectionSettings c, string f, IReadOnlyList<uint> u, uint? v = null, CancellationToken t = default) => Task.FromResult(ImapOperationResult<IReadOnlyList<ImapMessageHeader>>.Success(DestinationHeaders.Where(x => u.Contains(x.Uid)).ToArray()));
        public Task<ImapOperationResult<int>> DeleteAndExpungeAsync(ImapConnectionSettings c, string f, IReadOnlyList<uint> u, uint v, CancellationToken t = default) { DeleteCalls++; DeletedUid = Assert.Single(u); DeletedUidValidity = v; DeletedFolder = f; return Task.FromResult(DeleteFailure ? ImapOperationResult<int>.Failure(ImapFailureKind.Transient, "safe failure") : ImapOperationResult<int>.Success(1)); } public Task<ImapOperationResult<bool>> TestConnectionAsync(ImapConnectionSettings c, CancellationToken t = default) => throw new NotSupportedException();
    }
}
