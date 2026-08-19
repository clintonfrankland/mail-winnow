using System.Security.Claims;
using MailWinnow.Core.Rules;
using MailWinnow.Infrastructure.Mailboxes;
using MailWinnow.Infrastructure.Persistence;
using MailWinnow.Infrastructure.Rules;
using MailWinnow.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MimeKit;

namespace MailWinnow.Tests.Rules;

public sealed class ReviewMessagePreviewServiceTests
{
    [Fact]
    public async Task ReadsOnlyOwnedPendingItemUsingItsExactSourceIdentityAndSafeHtml()
    {
        await using var fixture = await Fixture.CreateAsync("owner");
        var header = await fixture.AddHeaderAsync("owner", "Archive/2026", 17, 99);
        var imap = new RecordingImap { Message = HtmlMessage() };
        var service = fixture.CreateService(imap, header.Id);

        var result = await service.ReadAsync(Principal("owner"), header.Id);

        Assert.True(result.Succeeded);
        Assert.Equal(("Archive/2026", 17u, 99u), imap.Fetch);
        var html = result.Value!.HtmlBody;
        Assert.Contains("<strong>safe</strong>", html);
        Assert.DoesNotContain("evil()", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://remote.test/pixel.png", html);
        Assert.Contains("data:image/gif;base64", html);
        Assert.Contains("data:image/png;base64,AQID", html);
        Assert.Contains("default-src 'none'", html);
        Assert.Contains("img-src data:", html);
        Assert.Contains("referrer\" content=\"no-referrer", html);
        Assert.Contains("target=\"_blank\"", html);
        Assert.Contains("rel=\"noopener noreferrer\"", html);
    }

    [Fact]
    public async Task RejectsForgedOrOtherOwnersItemWithoutOpeningImap()
    {
        await using var fixture = await Fixture.CreateAsync("owner");
        var other = await fixture.AddHeaderAsync("other", "INBOX", 2, 3);
        var imap = new RecordingImap();
        var service = fixture.CreateService(imap, other.Id);

        var result = await service.ReadAsync(Principal("owner"), other.Id);

        Assert.False(result.Succeeded);
        Assert.Null(imap.Fetch);
        Assert.DoesNotContain("other", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UsesSafePlainTextFallbackAndReturnsSanitizedFetchFailure()
    {
        await using var fixture = await Fixture.CreateAsync("owner");
        var header = await fixture.AddHeaderAsync("owner", "INBOX", 4, 8);
        var imap = new RecordingImap { Message = PlainTextMessage("one\n<two>") };
        var service = fixture.CreateService(imap, header.Id);

        var result = await service.ReadAsync(Principal("owner"), header.Id);

        Assert.True(result.Succeeded);
        Assert.Contains("<pre>", result.Value!.HtmlBody);
        Assert.Contains("&lt;two&gt;", result.Value.HtmlBody);

        imap.Fail = true;
        var failed = await service.ReadAsync(Principal("owner"), header.Id);
        Assert.False(failed.Succeeded);
        Assert.Equal("The source message could not be loaded. Try again after the next mailbox sync.", failed.Error);
        Assert.DoesNotContain("server", failed.Error!, StringComparison.OrdinalIgnoreCase);
    }

    private static ClaimsPrincipal Principal(string owner) => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, owner)], "test"));
    private static MimeMessage HtmlMessage()
    {
        var message = new MimeMessage { Subject = "Preview" };
        message.From.Add(MailboxAddress.Parse("Sender <sender@test>"));
        message.To.Add(MailboxAddress.Parse("Owner <owner@test>"));
        message.Body = new Multipart("related")
        {
            new TextPart("html") { Text = "<p onclick=\"evil()\">Hello <strong>safe</strong></p><script>evil()</script><a href=\"https://example.test\">link</a><img src=\"https://remote.test/pixel.png\"><img src=\"cid:logo@test\">" },
            new MimePart("image", "png") { ContentId = "logo@test", Content = new MimeContent(new MemoryStream([1, 2, 3])) }
        };
        return message;
    }
    private static MimeMessage PlainTextMessage(string text)
    {
        var message = new MimeMessage { Subject = "Plain", Body = new TextPart("plain") { Text = text } };
        message.From.Add(MailboxAddress.Parse("Sender <sender@test>"));
        message.To.Add(MailboxAddress.Parse("Owner <owner@test>"));
        return message;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly MailWinnowDbContext _db;
        private Fixture(SqliteConnection connection, MailWinnowDbContext db) { _connection = connection; _db = db; }
        public static async Task<Fixture> CreateAsync(string owner)
        {
            var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
            var db = new MailWinnowDbContext(new DbContextOptionsBuilder<MailWinnowDbContext>().UseSqlite(connection).Options); await db.Database.EnsureCreatedAsync();
            return new(connection, db);
        }
        public async Task<SourceMessageHeader> AddHeaderAsync(string owner, string folder, uint uid, uint validity)
        {
            var source = new SourceMailbox { OwnerUserId = owner, DisplayName = "Source", Host = "imap.test", Port = 993, UseSsl = true, Username = owner, ProtectedCredential = "credential" };
            var header = new SourceMessageHeader { SourceMailboxId = source.Id, FolderName = folder, Uid = uid, UidValidity = validity, From = "sender@test", Subject = "Pending", ReceivedUtc = DateTimeOffset.UtcNow };
            _db.AddRange(source, header); await _db.SaveChangesAsync(); return header;
        }
        public ReviewMessagePreviewService CreateService(RecordingImap imap, params Guid[] pending) => new(_db, new OwnershipAuthorizer(), new Protector(), imap, new PendingReviews(pending));
        public async ValueTask DisposeAsync() { await _db.DisposeAsync(); await _connection.DisposeAsync(); }
    }
    private sealed class Protector : ICredentialProtectionService { public string Protect(string value, CredentialKind kind) => value; public string Unprotect(string value, CredentialKind kind) => value; }
    private sealed class PendingReviews(IEnumerable<Guid> pending) : IMessageReviewService
    {
        public Task<IReadOnlyList<MessageReviewItem>> GetRecentAsync(ClaimsPrincipal user, MessageReviewFilter filter, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MessageReviewItem>>(pending.Select(id => new MessageReviewItem(id, "sender@test", "Pending", "Source", DateTimeOffset.UtcNow, "", RuleOutcome.Pending, "", "", "")).ToList());
        public Task<IReadOnlyList<MessageReviewGroup>> GetBySenderAsync(ClaimsPrincipal user, MessageReviewFilter filter, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<MessageReviewGroup>> GetBySubjectAsync(ClaimsPrincipal user, MessageReviewFilter filter, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ReviewRule>> GetRulesAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class RecordingImap : IImapConnectionService
    {
        public MimeMessage? Message { get; init; }
        public bool Fail { get; set; }
        public (string Folder, uint Uid, uint Validity)? Fetch { get; private set; }
        public Task<ImapOperationResult<MimeMessage>> FetchMessageAsync(ImapConnectionSettings connection, string folderName, uint uid, uint? expectedUidValidity = null, CancellationToken cancellationToken = default)
        {
            Fetch = (folderName, uid, expectedUidValidity ?? 0);
            return Task.FromResult(Fail ? ImapOperationResult<MimeMessage>.Failure(ImapFailureKind.Timeout, "raw server failure") : ImapOperationResult<MimeMessage>.Success(Message!));
        }
        public Task<ImapOperationResult<IReadOnlyList<string>>> ListFoldersAsync(ImapConnectionSettings connection, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImapOperationResult<IReadOnlyList<ImapMessageHeader>>> FetchHeadersAsync(ImapConnectionSettings connection, string folderName, IReadOnlyList<uint> uids, uint? expectedUidValidity = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImapOperationResult<uint?>> AppendMessageAsync(ImapConnectionSettings connection, string folderName, MimeMessage message, CancellationToken cancellationToken = default, DateTimeOffset? receivedUtc = null) => throw new NotSupportedException();
        public Task<ImapOperationResult<int>> DeleteAndExpungeAsync(ImapConnectionSettings connection, string folderName, IReadOnlyList<uint> expiredUids, uint expectedUidValidity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImapOperationResult<bool>> TestConnectionAsync(ImapConnectionSettings connection, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImapOperationResult<ImapFolderSnapshot>> GetFolderSnapshotAsync(ImapConnectionSettings connection, string folderName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
