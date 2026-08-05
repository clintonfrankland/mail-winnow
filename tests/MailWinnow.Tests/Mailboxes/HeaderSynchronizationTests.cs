using System.Text.Json;
using MailWinnow.Infrastructure.Mailboxes;
using MailWinnow.Infrastructure.Persistence;
using MailWinnow.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MailWinnow.Tests.Mailboxes;

public sealed class HeaderSynchronizationTests
{
    [Fact]
    public async Task Repeated_sync_is_idempotent_and_stores_headers_only_once()
    {
        await using var fixture = await SyncFixture.CreateAsync();
        fixture.Imap.Snapshot = new(10, [1, 2]);
        fixture.Imap.Headers = [Header(1), Header(2)];

        await fixture.SynchronizeAsync();
        await fixture.SynchronizeAsync();

        Assert.Equal(2, await fixture.Db.SourceMessageHeaders.CountAsync());
        Assert.Equal(1, fixture.Imap.FetchCalls);
        Assert.All(await fixture.Db.SourceMessageHeaders.ToListAsync(), header => Assert.Equal(10u, header.UidValidity));
    }

    [Fact]
    public async Task UidValidity_rollover_uses_a_new_header_identity_namespace()
    {
        await using var fixture = await SyncFixture.CreateAsync();
        fixture.Imap.Snapshot = new(10, [1]);
        fixture.Imap.Headers = [Header(1)];
        await fixture.SynchronizeAsync();

        fixture.Imap.Snapshot = new(20, [1]);
        fixture.Imap.Headers = [Header(1)];
        await fixture.SynchronizeAsync();

        Assert.Equal(2, await fixture.Db.SourceMessageHeaders.CountAsync());
        Assert.Equal([10u, 20u], (await fixture.Db.SourceMessageHeaders.Select(x => x.UidValidity).Distinct().Order().ToListAsync()));
        Assert.Equal(20u, (await fixture.Db.SourceMailboxFolderSyncStates.SingleAsync()).UidValidity);
    }

    [Fact]
    public async Task Unavailable_account_lock_skips_overlapping_sync()
    {
        await using var fixture = await SyncFixture.CreateAsync(new UnavailableLockProvider());
        fixture.Imap.Snapshot = new(10, [1]);
        fixture.Imap.Headers = [Header(1)];

        await fixture.SynchronizeAsync();

        Assert.Equal(0, fixture.Imap.FetchCalls);
        Assert.Null((await fixture.Db.SourceMailboxes.SingleAsync()).LastSyncAttemptUtc);
    }

    [Fact]
    public async Task Failure_is_recorded_for_one_account_without_preventing_another_account_from_syncing()
    {
        await using var failed = await SyncFixture.CreateAsync();
        await using var healthy = await SyncFixture.CreateAsync();
        failed.Imap.FailNextSnapshot = true;
        healthy.Imap.Snapshot = new(10, [1]);
        healthy.Imap.Headers = [Header(1)];

        await failed.SynchronizeAsync();
        await healthy.SynchronizeAsync();

        Assert.Equal("Failed", (await failed.Db.SourceMailboxes.SingleAsync()).PollingStatus);
        Assert.Equal("Synchronized", (await healthy.Db.SourceMailboxes.SingleAsync()).PollingStatus);
        Assert.Single(await healthy.Db.SourceMessageHeaders.ToListAsync());
    }

    private static ImapMessageHeader Header(uint uid) => new(uid, $"<{uid}@test>", null, "from@test", null, null, "to@test", null, "subject", null);

    private sealed class SyncFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ISourceMailboxSyncLockProvider _locks;
        public MailWinnowDbContext Db { get; }
        public SourceMailbox Source { get; }
        public FakeImap Imap { get; } = new();

        private SyncFixture(SqliteConnection connection, MailWinnowDbContext db, SourceMailbox source, ISourceMailboxSyncLockProvider locks)
            => (_connection, Db, Source, _locks) = (connection, db, source, locks);

        public static async Task<SyncFixture> CreateAsync(ISourceMailboxSyncLockProvider? locks = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new MailWinnowDbContext(new DbContextOptionsBuilder<MailWinnowDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var source = new SourceMailbox
            {
                OwnerUserId = "owner", DisplayName = "source", Host = "imap.test", Port = 993, UseSsl = true,
                Username = "source@test", ProtectedCredential = "secret", SelectedFoldersJson = JsonSerializer.Serialize(new[] { "INBOX" })
            };
            db.SourceMailboxes.Add(source);
            await db.SaveChangesAsync();
            return new(connection, db, source, locks ?? new AvailableLockProvider());
        }

        public Task SynchronizeAsync(Guid? sourceId = null) => new SourceMailboxSynchronizer(Db, new PassthroughProtector(), Imap,
            Options.Create(new MailSyncOptions { BatchSize = 100 }), _locks).SynchronizeAsync(sourceId ?? Source.Id);

        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await _connection.DisposeAsync(); }
    }

    private sealed class AvailableLockProvider : ISourceMailboxSyncLockProvider
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(MailWinnowDbContext db, Guid sourceMailboxId, CancellationToken cancellationToken = default) => Task.FromResult<IAsyncDisposable?>(new Lease());
        private sealed class Lease : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }
    private sealed class UnavailableLockProvider : ISourceMailboxSyncLockProvider
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(MailWinnowDbContext db, Guid sourceMailboxId, CancellationToken cancellationToken = default) => Task.FromResult<IAsyncDisposable?>(null);
    }
    private sealed class PassthroughProtector : ICredentialProtectionService
    {
        public string Protect(string credential, CredentialKind kind) => credential;
        public string Unprotect(string protectedCredential, CredentialKind kind) => protectedCredential;
    }
    private sealed class FakeImap : IImapConnectionService
    {
        public ImapFolderSnapshot Snapshot { get; set; } = new(1, []);
        public IReadOnlyList<ImapMessageHeader> Headers { get; set; } = [];
        public int FetchCalls { get; private set; }
        public bool FailNextSnapshot { get; set; }
        public Task<ImapOperationResult<ImapFolderSnapshot>> GetFolderSnapshotAsync(ImapConnectionSettings connection, string folderName, CancellationToken cancellationToken = default)
        {
            if (FailNextSnapshot) { FailNextSnapshot = false; return Task.FromResult(ImapOperationResult<ImapFolderSnapshot>.Failure(ImapFailureKind.Transient, "test")); }
            return Task.FromResult(ImapOperationResult<ImapFolderSnapshot>.Success(Snapshot));
        }
        public Task<ImapOperationResult<IReadOnlyList<ImapMessageHeader>>> FetchHeadersAsync(ImapConnectionSettings connection, string folderName, IReadOnlyList<uint> uids, uint? expectedUidValidity = null, CancellationToken cancellationToken = default)
        { FetchCalls++; return Task.FromResult(ImapOperationResult<IReadOnlyList<ImapMessageHeader>>.Success(Headers)); }
        public Task<ImapOperationResult<IReadOnlyList<string>>> ListFoldersAsync(ImapConnectionSettings connection, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImapOperationResult<MimeKit.MimeMessage>> FetchMessageAsync(ImapConnectionSettings connection, string folderName, uint uid, uint? expectedUidValidity = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImapOperationResult<uint?>> AppendMessageAsync(ImapConnectionSettings connection, string folderName, MimeKit.MimeMessage message, CancellationToken cancellationToken = default, DateTimeOffset? receivedUtc = null) => throw new NotSupportedException();
        public Task<ImapOperationResult<int>> DeleteAndExpungeAsync(ImapConnectionSettings connection, string folderName, IReadOnlyList<uint> expiredUids, uint expectedUidValidity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImapOperationResult<bool>> TestConnectionAsync(ImapConnectionSettings connection, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
