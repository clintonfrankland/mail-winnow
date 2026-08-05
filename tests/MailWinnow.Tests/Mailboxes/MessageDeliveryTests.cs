using MailWinnow.Core.Rules;
using MailWinnow.Infrastructure.Mailboxes;
using MailWinnow.Infrastructure.Persistence;
using MailWinnow.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimeKit;

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
        Assert.NotNull(delivery.ExpiresUtc);
        Assert.Equal(1, f.Imap.AppendCalls);
        Assert.DoesNotContain(typeof(MessageDelivery).GetProperties(), x => x.PropertyType == typeof(MimeMessage));
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

    private sealed class Fixture(SqliteConnection connection, MailWinnowDbContext db, SourceMessageHeader header, FakeImap imap, MessageDeliveryService service) : IAsyncDisposable
    {
        public MailWinnowDbContext Db { get; } = db; public SourceMessageHeader Header { get; } = header; public FakeImap Imap { get; } = imap; public MessageDeliveryService Service { get; } = service;
        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
            var db = new MailWinnowDbContext(new DbContextOptionsBuilder<MailWinnowDbContext>().UseSqlite(connection).Options); await db.Database.EnsureCreatedAsync();
            var source = new SourceMailbox { OwnerUserId = "owner", DisplayName = "source", Host = "source.test", Port = 993, UseSsl = true, Username = "source", ProtectedCredential = "source" };
            var destination = new DestinationMailbox { OwnerUserId = "owner", Username = "destination", Folder = "INBOX", ProtectedCredential = "destination" };
            var header = new SourceMessageHeader { SourceMailboxId = source.Id, FolderName = "INBOX", UidValidity = 1, Uid = 10, ReceivedUtc = DateTimeOffset.UtcNow, EvaluationOutcome = RuleOutcome.Allow };
            db.AddRange(source, destination, header); await db.SaveChangesAsync();
            header.EvaluationOutcome = RuleOutcome.Allow; await db.SaveChangesAsync();
            var imap = new FakeImap();
            var service = new MessageDeliveryService(db, new Protector(), imap, Options.Create(new LocalImapOptions { Host = "local.test", Port = 993, UseSsl = true }), new OwnershipAuthorizer());
            return new(connection, db, header, imap, service);
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await connection.DisposeAsync(); }
    }
    private sealed class Protector : ICredentialProtectionService { public string Protect(string value, CredentialKind kind) => value; public string Unprotect(string value, CredentialKind kind) => value; }
    private sealed class FakeImap : IImapConnectionService
    {
        public bool FetchFailure { get; set; } public int AppendCalls { get; private set; }
        public Task<ImapOperationResult<MimeMessage>> FetchMessageAsync(ImapConnectionSettings c, string f, uint u, uint? v = null, CancellationToken t = default) => Task.FromResult(FetchFailure ? ImapOperationResult<MimeMessage>.Failure(ImapFailureKind.Transient, "safe failure") : ImapOperationResult<MimeMessage>.Success(new MimeMessage { Subject = "original" }));
        public Task<ImapOperationResult<uint?>> AppendMessageAsync(ImapConnectionSettings c, string f, MimeMessage m, CancellationToken t = default) { AppendCalls++; return Task.FromResult(ImapOperationResult<uint?>.Success(77)); }
        public Task<ImapOperationResult<ImapFolderSnapshot>> GetFolderSnapshotAsync(ImapConnectionSettings c, string f, CancellationToken t = default) => Task.FromResult(ImapOperationResult<ImapFolderSnapshot>.Success(new(42, [77])));
        public Task<ImapOperationResult<IReadOnlyList<string>>> ListFoldersAsync(ImapConnectionSettings c, CancellationToken t = default) => throw new NotSupportedException(); public Task<ImapOperationResult<IReadOnlyList<ImapMessageHeader>>> FetchHeadersAsync(ImapConnectionSettings c, string f, IReadOnlyList<uint> u, uint? v = null, CancellationToken t = default) => throw new NotSupportedException(); public Task<ImapOperationResult<int>> DeleteAndExpungeAsync(ImapConnectionSettings c, string f, IReadOnlyList<uint> u, CancellationToken t = default) => throw new NotSupportedException(); public Task<ImapOperationResult<bool>> TestConnectionAsync(ImapConnectionSettings c, CancellationToken t = default) => throw new NotSupportedException();
    }
}
