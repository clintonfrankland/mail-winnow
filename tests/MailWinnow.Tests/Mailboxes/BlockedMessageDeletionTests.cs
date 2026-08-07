using MailWinnow.Core.Rules;
using MailWinnow.Infrastructure.Mailboxes;
using MailWinnow.Infrastructure.Persistence;
using MailWinnow.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MimeKit;

namespace MailWinnow.Tests.Mailboxes;

public sealed class BlockedMessageDeletionTests
{
    [Fact]
    public async Task OnlyBlockedHeadersAreDueAndExactSourceIdentityIsDeleted()
    {
        await using var fixture = await Fixture.CreateAsync();
        var pending = await fixture.AddHeaderAsync(RuleOutcome.Pending, 11);
        var allowed = await fixture.AddHeaderAsync(RuleOutcome.Allow, 12);
        var blocked = await fixture.AddHeaderAsync(RuleOutcome.Block, 13);

        var due = await fixture.Service.GetDueHeaderIdsAsync();
        await fixture.Service.DeleteAsync(Assert.Single(due));

        Assert.DoesNotContain(pending.Id, due);
        Assert.DoesNotContain(allowed.Id, due);
        Assert.Equal(blocked.Id, Assert.Single(due));
        Assert.Equal("INBOX", fixture.Imap.DeletedFolder);
        Assert.Equal(13u, fixture.Imap.DeletedUid);
        Assert.Equal(42u, fixture.Imap.DeletedUidValidity);
        Assert.NotNull((await fixture.Db.SourceMessageHeaders.FindAsync(blocked.Id))!.BlockedSourceDeletedUtc);
        Assert.Empty(await fixture.Service.GetDueHeaderIdsAsync());
    }

    [Fact]
    public async Task FailedBlockedDeletionIsSanitizedAndRemainsRetryable()
    {
        await using var fixture = await Fixture.CreateAsync();
        var blocked = await fixture.AddHeaderAsync(RuleOutcome.Block, 13);
        fixture.Imap.DeleteFailure = true;

        await fixture.Service.DeleteAsync(blocked.Id);

        var stored = await fixture.Db.SourceMessageHeaders.FindAsync(blocked.Id);
        Assert.Null(stored!.BlockedSourceDeletedUtc);
        Assert.Null(stored.BlockedSourceDeletionStartedUtc);
        Assert.Equal("safe failure", stored.BlockedSourceDeletionError);
        Assert.Contains(blocked.Id, await fixture.Service.GetDueHeaderIdsAsync());

        fixture.Imap.DeleteFailure = false;
        await fixture.Service.DeleteAsync(blocked.Id);
        Assert.NotNull((await fixture.Db.SourceMessageHeaders.FindAsync(blocked.Id))!.BlockedSourceDeletedUtc);
        Assert.Equal(2, fixture.Imap.DeleteCalls);
    }

    private sealed class Fixture(SqliteConnection connection, MailWinnowDbContext db, SourceMailbox source, FakeImap imap, BlockedMessageDeletionService service) : IAsyncDisposable
    {
        public MailWinnowDbContext Db { get; } = db;
        public FakeImap Imap { get; } = imap;
        public BlockedMessageDeletionService Service { get; } = service;
        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new MailWinnowDbContext(new DbContextOptionsBuilder<MailWinnowDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var source = new SourceMailbox { OwnerUserId = "owner", DisplayName = "source", Host = "source.test", Port = 993, UseSsl = true, Username = "owner", ProtectedCredential = "secret" };
            db.SourceMailboxes.Add(source);
            await db.SaveChangesAsync();
            var imap = new FakeImap();
            return new(connection, db, source, imap, new BlockedMessageDeletionService(db, new Protector(), imap));
        }
        public async Task<SourceMessageHeader> AddHeaderAsync(RuleOutcome outcome, uint uid)
        {
            var header = new SourceMessageHeader { SourceMailboxId = source.Id, FolderName = "INBOX", UidValidity = 42, Uid = uid, ReceivedUtc = DateTimeOffset.UtcNow, EvaluationOutcome = outcome };
            Db.SourceMessageHeaders.Add(header);
            await Db.SaveChangesAsync();
            return header;
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await connection.DisposeAsync(); }
    }

    private sealed class Protector : ICredentialProtectionService { public string Protect(string value, CredentialKind kind) => value; public string Unprotect(string value, CredentialKind kind) => value; }
    private sealed class FakeImap : IImapConnectionService
    {
        public bool DeleteFailure { get; set; }
        public int DeleteCalls { get; private set; }
        public string? DeletedFolder { get; private set; }
        public uint DeletedUid { get; private set; }
        public uint DeletedUidValidity { get; private set; }
        public Task<ImapOperationResult<int>> DeleteAndExpungeAsync(ImapConnectionSettings connection, string folderName, IReadOnlyList<uint> expiredUids, uint expectedUidValidity, CancellationToken cancellationToken = default)
        {
            DeleteCalls++; DeletedFolder = folderName; DeletedUid = Assert.Single(expiredUids); DeletedUidValidity = expectedUidValidity;
            return Task.FromResult(DeleteFailure ? ImapOperationResult<int>.Failure(ImapFailureKind.Transient, "safe failure") : ImapOperationResult<int>.Success(1));
        }
        public Task<ImapOperationResult<IReadOnlyList<string>>> ListFoldersAsync(ImapConnectionSettings connection, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImapOperationResult<IReadOnlyList<ImapMessageHeader>>> FetchHeadersAsync(ImapConnectionSettings connection, string folderName, IReadOnlyList<uint> uids, uint? expectedUidValidity = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImapOperationResult<MimeMessage>> FetchMessageAsync(ImapConnectionSettings connection, string folderName, uint uid, uint? expectedUidValidity = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImapOperationResult<uint?>> AppendMessageAsync(ImapConnectionSettings connection, string folderName, MimeMessage message, CancellationToken cancellationToken = default, DateTimeOffset? receivedUtc = null) => throw new NotSupportedException();
        public Task<ImapOperationResult<bool>> TestConnectionAsync(ImapConnectionSettings connection, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImapOperationResult<ImapFolderSnapshot>> GetFolderSnapshotAsync(ImapConnectionSettings connection, string folderName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
