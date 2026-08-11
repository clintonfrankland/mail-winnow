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
    public async Task ListsOwnedDestinationAndBuildsSanitizedIsolatedHtmlMessage()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new MailWinnowDbContext(new DbContextOptionsBuilder<MailWinnowDbContext>().UseSqlite(connection).Options); await db.Database.EnsureCreatedAsync();
        var destination = new DestinationMailbox { OwnerUserId = "owner", Username = "local", ProtectedCredential = "password", Folder = "INBOX" };
        db.DestinationMailboxes.Add(destination);
        var delivered = DateTimeOffset.UtcNow.AddDays(-1);
        db.MessageDeliveries.Add(new MessageDelivery
        {
            OwnerUserId = "owner", SourceMessageHeaderId = Guid.NewGuid(), DestinationMailboxId = destination.Id,
            DestinationFolder = "INBOX", DestinationUid = 2, DestinationUidValidity = 42,
            State = MessageDeliveryState.Delivered, DeliveredUtc = delivered, ExpiresUtc = delivered.AddDays(7)
        });
        await db.SaveChangesAsync();
        var imap = new FakeImap();
        var service = new InboxReaderService(db, new OwnershipAuthorizer(), new Protector(), Options.Create(new LocalImapOptions { Host = "imap.test", Port = 993, UseSsl = true }), imap);

        var list = await service.ListAsync(Principal("owner"));
        var message = await service.ReadAsync(Principal("owner"), 2, 42);

        Assert.True(list.Succeeded);
        var listed = Assert.IsAssignableFrom<IReadOnlyList<InboxMessageSummary>>(list.Value);
        Assert.Equal([2u, 1u], listed.Select(x => x.Uid));
        Assert.All(listed, item => Assert.NotEqual(default, item.Date));
        Assert.Equal(imap.NewestInternalDate, listed[0].Date);
        Assert.Equal("1 week", listed[0].RetentionLabel);
        Assert.Null(listed[1].RetentionLabel);
        Assert.True(message.Succeeded);
        Assert.True(message.Value!.HasRemoteImages);
        Assert.Contains("<strong>world</strong>", message.Value.HtmlBody);
        Assert.Contains("data:image/gif;base64", message.Value.HtmlBody);
        Assert.DoesNotContain("https://images.test/pixel.png", message.Value.HtmlBody);
        Assert.Contains("https://images.test/pixel.png", message.Value.HtmlBodyWithRemoteImages);
        Assert.Contains("data:image/png;base64,AQID", message.Value.HtmlBodyWithRemoteImages);
        Assert.DoesNotContain("<script", message.Value.HtmlBodyWithRemoteImages, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bad()", message.Value.HtmlBodyWithRemoteImages);
        Assert.DoesNotContain("onclick", message.Value.HtmlBodyWithRemoteImages, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("default-src 'none'", message.Value.HtmlBody);
        Assert.Equal((2u, 42u), imap.FetchedIdentity);
        Assert.Equal("local", imap.Connection!.Username);
    }

    [Theory]
    [InlineData(null, "Forever")]
    [InlineData(30, "1 month")]
    [InlineData(7, "1 week")]
    [InlineData(3, "3 days")]
    [InlineData(1, "1 day")]
    public void MapsTrackedDeliveryExpiryToProductRetentionLabel(int? days, string expected)
    {
        var delivered = DateTimeOffset.Parse("2026-08-11T00:00:00Z");

        Assert.Equal(expected, InboxReaderService.RetentionLabel(delivered, days is null ? null : delivered.AddDays(days.Value)));
    }

    [Fact]
    public void OmitsLabelWhenTrackedExpiryCannotBeMapped()
    {
        var delivered = DateTimeOffset.Parse("2026-08-11T00:00:00Z");

        Assert.Null(InboxReaderService.RetentionLabel(delivered, delivered.AddDays(2)));
        Assert.Null(InboxReaderService.RetentionLabel(null, delivered.AddDays(1)));
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
        public DateTimeOffset NewestInternalDate { get; } = DateTimeOffset.UtcNow;
        public ImapConnectionSettings? Connection { get; private set; }
        public (uint Uid, uint Validity) FetchedIdentity { get; private set; }
        public Task<ImapOperationResult<ImapFolderSnapshot>> GetFolderSnapshotAsync(ImapConnectionSettings c, string f, CancellationToken t = default) { Connection = c; return Task.FromResult(ImapOperationResult<ImapFolderSnapshot>.Success(new(42, [1, 2]))); }
        public Task<ImapOperationResult<IReadOnlyList<ImapMessageHeader>>> FetchHeadersAsync(ImapConnectionSettings c, string f, IReadOnlyList<uint> u, uint? v = null, CancellationToken t = default)
        {
            ImapMessageHeader[] headers = [
                new(1, null, DateTimeOffset.UtcNow.AddMinutes(-2), "Old <old@test>", null, null, null, null, "Old", null, DateTimeOffset.UtcNow.AddMinutes(-1)),
                new(2, null, null, "New <new@test>", null, null, null, null, "New", null, NewestInternalDate)];
            return Task.FromResult(ImapOperationResult<IReadOnlyList<ImapMessageHeader>>.Success(headers.Where(x => u.Contains(x.Uid)).ToArray()));
        }
        public Task<ImapOperationResult<MimeMessage>> FetchMessageAsync(ImapConnectionSettings c, string f, uint u, uint? v = null, CancellationToken t = default) { FetchedIdentity = (u, v ?? 0); var related = new Multipart("related") { new TextPart("html") { Text = "<p onclick=\"steal()\">Hello <strong>world</strong></p><img src=\"https://images.test/pixel.png\"><img src=\"cid:logo@test\"><script>bad()</script>" }, new MimePart("image", "png") { ContentId = "logo@test", Content = new MimeContent(new MemoryStream([1, 2, 3])) } }; var m = new MimeMessage { Subject = "New", Body = related }; m.From.Add(MailboxAddress.Parse("New <new@test>")); m.To.Add(MailboxAddress.Parse("Owner <owner@test>")); return Task.FromResult(ImapOperationResult<MimeMessage>.Success(m)); }
        public Task<ImapOperationResult<IReadOnlyList<string>>> ListFoldersAsync(ImapConnectionSettings c, CancellationToken t = default) => throw new NotSupportedException();
        public Task<ImapOperationResult<uint?>> AppendMessageAsync(ImapConnectionSettings c, string f, MimeMessage m, CancellationToken t = default, DateTimeOffset? d = null) => throw new NotSupportedException();
        public Task<ImapOperationResult<int>> DeleteAndExpungeAsync(ImapConnectionSettings c, string f, IReadOnlyList<uint> u, uint v, CancellationToken t = default) => throw new NotSupportedException();
        public Task<ImapOperationResult<bool>> TestConnectionAsync(ImapConnectionSettings c, CancellationToken t = default) => throw new NotSupportedException();
    }
}
