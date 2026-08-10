using MailWinnow.Infrastructure.Mailboxes;
using MailWinnow.Infrastructure.Persistence;
using MailWinnow.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;

namespace MailWinnow.Tests.Mailboxes;

public sealed class MailboxRetentionTests
{
    [Fact]
    public async Task EnabledDestinationsCleanBlockedAfter14DaysAndTrashAfter30Days()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new MailWinnowDbContext(new DbContextOptionsBuilder<MailWinnowDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.DestinationMailboxes.Add(new DestinationMailbox { OwnerUserId = "owner", Username = "local", ProtectedCredential = "secret", Folder = "INBOX" });
        db.DestinationMailboxes.Add(new DestinationMailbox { OwnerUserId = "disabled", Username = "disabled", ProtectedCredential = "secret", Folder = "INBOX", Enabled = false });
        await db.SaveChangesAsync();
        var imap = new RecordingImap();
        var before = DateTimeOffset.UtcNow;

        await new MailboxRetentionService(db, new PlainCredentials(), imap,
            Options.Create(new LocalImapOptions { Host = "local.test", Port = 993, UseSsl = true }),
            NullLogger<MailboxRetentionService>.Instance).RunAsync();

        var blocked = Assert.Single(imap.Calls, x => x.Folder == "Blocked");
        var trash = Assert.Single(imap.Calls, x => x.Folder == "Trash");
        Assert.InRange(blocked.Cutoff, before.AddDays(-14), DateTimeOffset.UtcNow.AddDays(-14));
        Assert.InRange(trash.Cutoff, before.AddDays(-30), DateTimeOffset.UtcNow.AddDays(-30));
        Assert.All(imap.Calls, call => Assert.True(call.CreateIfMissing));
        Assert.All(imap.Calls, call => Assert.Equal("local", call.Username));
    }

    private sealed class PlainCredentials : ICredentialProtectionService
    { public string Protect(string value, CredentialKind kind) => value; public string Unprotect(string value, CredentialKind kind) => value; }

    private sealed class RecordingImap : IImapConnectionService
    {
        public List<(string Username, string Folder, DateTimeOffset Cutoff, bool CreateIfMissing)> Calls { get; } = [];
        public Task<ImapOperationResult<int>> DeleteOlderThanAsync(ImapConnectionSettings connection, string folderName, DateTimeOffset cutoffUtc, bool createFolderIfMissing = false, CancellationToken cancellationToken = default)
        { Calls.Add((connection.Username, folderName, cutoffUtc, createFolderIfMissing)); return Task.FromResult(ImapOperationResult<int>.Success(0)); }
        public Task<ImapOperationResult<int>> DeleteAndExpungeAsync(ImapConnectionSettings connection, string folderName, IReadOnlyList<uint> expiredUids, uint expectedUidValidity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImapOperationResult<IReadOnlyList<string>>> ListFoldersAsync(ImapConnectionSettings connection, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImapOperationResult<IReadOnlyList<ImapMessageHeader>>> FetchHeadersAsync(ImapConnectionSettings connection, string folderName, IReadOnlyList<uint> uids, uint? expectedUidValidity = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImapOperationResult<MimeMessage>> FetchMessageAsync(ImapConnectionSettings connection, string folderName, uint uid, uint? expectedUidValidity = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImapOperationResult<uint?>> AppendMessageAsync(ImapConnectionSettings connection, string folderName, MimeMessage message, CancellationToken cancellationToken = default, DateTimeOffset? receivedUtc = null) => throw new NotSupportedException();
        public Task<ImapOperationResult<bool>> TestConnectionAsync(ImapConnectionSettings connection, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImapOperationResult<ImapFolderSnapshot>> GetFolderSnapshotAsync(ImapConnectionSettings connection, string folderName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
