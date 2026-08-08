using System.Security.Claims;
using MailWinnow.Infrastructure.Mailboxes;
using MailWinnow.Infrastructure.Persistence;
using MailWinnow.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimeKit;

namespace MailWinnow.Tests.Mailboxes;

public sealed class InboxReaderTests
{
    [Fact]
    public async Task ListsOwnedDestinationNewestFirstAndReadsBodyWithRemoteImagesSeparated()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new MailWinnowDbContext(new DbContextOptionsBuilder<MailWinnowDbContext>().UseSqlite(connection).Options); await db.Database.EnsureCreatedAsync();
        db.DestinationMailboxes.Add(new DestinationMailbox { OwnerUserId = "owner", Username = "local", ProtectedCredential = "password", Folder = "INBOX" });
        await db.SaveChangesAsync();
        var imap = new FakeImap();
        var service = new InboxReaderService(db, new OwnershipAuthorizer(), new Protector(), Options.Create(new LocalImapOptions { Host = "imap.test", Port = 993, UseSsl = true }), imap);

        var list = await service.ListAsync(Principal("owner"));
        var message = await service.ReadAsync(Principal("owner"), 2, 42);

        Assert.True(list.Succeeded);
        Assert.Equal([2u, 1u], list.Value!.Select(x => x.Uid));
        Assert.True(message.Succeeded);
        Assert.Equal("Hello world", message.Value!.Body);
        Assert.Equal(["https://images.test/pixel.png"], message.Value.RemoteImageUrls);
        Assert.Equal((2u, 42u), imap.FetchedIdentity);
        Assert.Equal("local", imap.Connection!.Username);
    }

    [Fact]
    public async Task DoesNotExposeAnotherUsersDestination()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new MailWinnowDbContext(new DbContextOptionsBuilder<MailWinnowDbContext>().UseSqlite(connection).Options); await db.Database.EnsureCreatedAsync();
        db.DestinationMailboxes.Add(new DestinationMailbox { OwnerUserId = "other", Username = "other", ProtectedCredential = "password", Folder = "INBOX" }); await db.SaveChangesAsync();
        var imap = new FakeImap();
        var service = new InboxReaderService(db, new OwnershipAuthorizer(), new Protector(), Options.Create(new LocalImapOptions { Host = "imap.test", Port = 993, UseSsl = true }), imap);

        var result = await service.ListAsync(Principal("owner"));

        Assert.False(result.Succeeded);
        Assert.Null(imap.Connection);
    }

    private static ClaimsPrincipal Principal(string id) => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "test"));
    private sealed class Protector : ICredentialProtectionService { public string Protect(string value, CredentialKind kind) => value; public string Unprotect(string value, CredentialKind kind) => value; }
    private sealed class FakeImap : IImapConnectionService
    {
        public ImapConnectionSettings? Connection { get; private set; }
        public (uint Uid, uint Validity) FetchedIdentity { get; private set; }
        public Task<ImapOperationResult<ImapFolderSnapshot>> GetFolderSnapshotAsync(ImapConnectionSettings c, string f, CancellationToken t = default) { Connection = c; return Task.FromResult(ImapOperationResult<ImapFolderSnapshot>.Success(new(42, [1, 2]))); }
        public Task<ImapOperationResult<IReadOnlyList<ImapMessageHeader>>> FetchHeadersAsync(ImapConnectionSettings c, string f, IReadOnlyList<uint> u, uint? v = null, CancellationToken t = default) => Task.FromResult(ImapOperationResult<IReadOnlyList<ImapMessageHeader>>.Success([
            new(1, null, DateTimeOffset.UtcNow.AddMinutes(-2), "Old <old@test>", null, null, null, null, "Old", null),
            new(2, null, DateTimeOffset.UtcNow, "New <new@test>", null, null, null, null, "New", null)]));
        public Task<ImapOperationResult<MimeMessage>> FetchMessageAsync(ImapConnectionSettings c, string f, uint u, uint? v = null, CancellationToken t = default) { FetchedIdentity = (u, v ?? 0); var m = new MimeMessage { Subject = "New", Body = new TextPart("html") { Text = "<p>Hello <strong>world</strong></p><img src=\"https://images.test/pixel.png\"><script>bad()</script>" } }; m.From.Add(MailboxAddress.Parse("New <new@test>")); m.To.Add(MailboxAddress.Parse("Owner <owner@test>")); return Task.FromResult(ImapOperationResult<MimeMessage>.Success(m)); }
        public Task<ImapOperationResult<IReadOnlyList<string>>> ListFoldersAsync(ImapConnectionSettings c, CancellationToken t = default) => throw new NotSupportedException();
        public Task<ImapOperationResult<uint?>> AppendMessageAsync(ImapConnectionSettings c, string f, MimeMessage m, CancellationToken t = default, DateTimeOffset? d = null) => throw new NotSupportedException();
        public Task<ImapOperationResult<int>> DeleteAndExpungeAsync(ImapConnectionSettings c, string f, IReadOnlyList<uint> u, uint v, CancellationToken t = default) => throw new NotSupportedException();
        public Task<ImapOperationResult<bool>> TestConnectionAsync(ImapConnectionSettings c, CancellationToken t = default) => throw new NotSupportedException();
    }
}
