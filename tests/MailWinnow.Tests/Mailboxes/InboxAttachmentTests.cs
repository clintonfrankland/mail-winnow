using System.Security.Claims;
using System.Text;
using MailWinnow.Infrastructure.Mailboxes;
using MailWinnow.Infrastructure.Persistence;
using MailWinnow.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimeKit;

namespace MailWinnow.Tests.Mailboxes;

public sealed class InboxAttachmentTests
{
    [Fact]
    public async Task DecodesBytesAndUsesExactOwnerMailboxIdentity()
    {
        await using var fixture = await Fixture.CreateAsync();
        var bytes = new byte[] { 0, 1, 127, 128, 255 };
        fixture.Imap.Message = new MimeMessage { Body = new Multipart("mixed") {
            new TextPart("plain") { Text = "Body" }, new MimePart("application", "pdf") {
                FileName = "report.pdf", ContentDisposition = new ContentDisposition("attachment") { FileName = "report.pdf" },
                Content = new MimeContent(new MemoryStream(Encoding.ASCII.GetBytes(Convert.ToBase64String(bytes))), ContentEncoding.Base64) } } };
        var result = await fixture.DownloadAsync();
        Assert.Equal(InboxAttachmentDownloadStatus.Success, result.Status);
        await using var content = result.Content!;
        Assert.Equal(0, content.Position);
        using var copied = new MemoryStream(); await content.CopyToAsync(copied);
        Assert.Equal(bytes, copied.ToArray()); Assert.Equal("report.pdf", result.FileName);
        Assert.Equal(("owner-inbox", "INBOX", 7u, 42u), fixture.Imap.Identity);
    }

    [Fact]
    public void MetadataExcludesInlineImagesWithoutDecodingAndKeepsStableIndices()
    {
        using var message = new MimeMessage { Body = new Multipart("mixed") {
            new TextPart("plain") { Text = "Body" },
            new MimePart("image", "png") { ContentId = "inline", ContentType = { Name = "logo.png" }, Content = new MimeContent(new UnreadableStream()) },
            new MimePart("image", "png") { FileName = "inline.png", ContentDisposition = new ContentDisposition("inline"), Content = new MimeContent(new UnreadableStream()) },
            File("one.txt", new UnreadableStream()),
            new Multipart("mixed") { File("two.zip", new UnreadableStream()) } } };
        var attachments = InboxAttachmentService.DescribeAttachments(message);
        Assert.Equal(["one.txt", "two.zip"], attachments.Select(item => item.FileName));
        Assert.Equal([0, 1], attachments.Select(item => item.Index));
    }

    [Fact]
    public async Task ReaderReturnsMailboxBoundMetadataWithoutDecodingAttachment()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Imap.Message = new MimeMessage { Body = new Multipart("mixed") {
            new TextPart("plain") { Text = "Body" }, File("report.pdf", new UnreadableStream()) } };
        var reader = new InboxReaderService(fixture.Database, new OwnershipAuthorizer(), new Protector(),
            Options.Create(new LocalImapOptions { Host = "imap.test", Port = 993, UseSsl = true }), fixture.Imap);
        var result = await reader.ReadAsync(Principal("owner"), 7, 42);
        Assert.True(result.Succeeded);
        Assert.Equal(fixture.Destination.Id, result.Value!.DestinationMailboxId);
        Assert.Equal("INBOX", result.Value.DestinationFolder);
        Assert.Equal(fixture.Identity, result.Value.AttachmentMailboxIdentity);
        Assert.Equal("report.pdf", Assert.Single(result.Value.Attachments!).FileName);
    }

    [Fact]
    public async Task AttachedMessageDownloadsAsEmlWithoutListingItsChildren()
    {
        await using var fixture = await Fixture.CreateAsync();
        var attached = new MimeMessage { Subject = "Forwarded", Body = new Multipart("mixed") {
            new TextPart("plain") { Text = "Nested body" }, File("nested.txt", new MemoryStream([1, 2])) } };
        fixture.Imap.Message = new MimeMessage { Body = new Multipart("mixed") {
            new TextPart("plain") { Text = "Outer body" },
            new MessagePart { Message = attached, ContentDisposition = new ContentDisposition("attachment") } } };
        var metadata = InboxAttachmentService.DescribeAttachments(fixture.Imap.Message);
        Assert.Single(metadata); Assert.Equal("message-1.eml", metadata[0].FileName);
        var result = await fixture.DownloadAsync();
        Assert.Equal(InboxAttachmentDownloadStatus.Success, result.Status);
        await using var content = result.Content!;
        using var parsed = await MimeMessage.LoadAsync(content);
        Assert.Equal("Forwarded", parsed.Subject); Assert.Equal("Nested body", parsed.TextBody);
    }

    [Theory]
    [InlineData("other", "INBOX", true)]
    [InlineData("owner", "OtherFolder", true)]
    [InlineData("owner", "inbox", true)]
    [InlineData("owner", "INBOX", false)]
    public async Task RejectsOtherOwnersChangedFoldersAndDisabledDestinationsBeforeImap(string owner, string folder, bool enabled)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Destination.Enabled = enabled; await fixture.Database.SaveChangesAsync();
        var result = await fixture.Service.DownloadAsync(Principal(owner), fixture.Destination.Id, folder, fixture.Identity, 7, 42, 0);
        Assert.Equal(InboxAttachmentDownloadStatus.NotFound, result.Status); Assert.Equal(0, fixture.Imap.FetchCount);
    }

    [Fact]
    public async Task RejectsChangedMailboxIdentifierBeforeImap()
    {
        await using var fixture = await Fixture.CreateAsync();
        var result = await fixture.Service.DownloadAsync(Principal("owner"), Guid.NewGuid(), "INBOX", fixture.Identity, 7, 42, 0);
        Assert.Equal(InboxAttachmentDownloadStatus.NotFound, result.Status); Assert.Equal(0, fixture.Imap.FetchCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OldLinkCannotReadReplacementAccountOrServer(bool changeUsername)
    {
        await using var fixture = await Fixture.CreateAsync();
        var oldIdentity = fixture.Identity;
        if (changeUsername) { fixture.Destination.Username = "replacement"; await fixture.Database.SaveChangesAsync(); }
        else fixture.Configuration = new LocalImapOptions { Host = "replacement.test", Port = 993, UseSsl = true };
        var result = await fixture.Service.DownloadAsync(Principal("owner"), fixture.Destination.Id, "INBOX", oldIdentity, 7, 42, 0);
        Assert.Equal(InboxAttachmentDownloadStatus.NotFound, result.Status); Assert.Equal(0, fixture.Imap.FetchCount);
    }

    [Fact]
    public void TransportClassifiesMissingMessageSeparatelyWithoutProviderDetails()
    {
        Assert.Equal(ImapFailureKind.MissingMessage,
            ImapConnectionService.ClassifyFailure(new MailKit.MessageNotFoundException("private provider error")));
    }

    [Theory]
    [InlineData(0u, 42u, 0)]
    [InlineData(7u, 0u, 0)]
    [InlineData(7u, 42u, -1)]
    public async Task RejectsInvalidCoordinatesBeforeImap(uint uid, uint validity, int index)
    {
        await using var fixture = await Fixture.CreateAsync();
        var result = await fixture.Service.DownloadAsync(Principal("owner"), fixture.Destination.Id, "INBOX", fixture.Identity, uid, validity, index);
        Assert.Equal(InboxAttachmentDownloadStatus.NotFound, result.Status); Assert.Equal(0, fixture.Imap.FetchCount);
    }

    [Theory]
    [InlineData(ImapFailureKind.UidValidityChanged, InboxAttachmentDownloadStatus.NotFound)]
    [InlineData(ImapFailureKind.MissingMessage, InboxAttachmentDownloadStatus.NotFound)]
    [InlineData(ImapFailureKind.Connection, InboxAttachmentDownloadStatus.Unavailable)]
    public async Task StaleIdentityAndProviderFailureNeverReturnBytes(ImapFailureKind failure, InboxAttachmentDownloadStatus expected)
    {
        await using var fixture = await Fixture.CreateAsync(); fixture.Imap.Failure = failure;
        var result = await fixture.DownloadAsync();
        Assert.Equal(expected, result.Status); Assert.Null(result.Content); Assert.Null(result.FileName);
        Assert.Equal(42u, fixture.Imap.Identity.Validity);
    }

    [Fact]
    public async Task MissingAttachmentReturnsNotFound()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Imap.Message = new MimeMessage { Body = File("only.txt", new MemoryStream([1])) };
        var result = await fixture.Service.DownloadAsync(Principal("owner"), fixture.Destination.Id, "INBOX", fixture.Identity, 7, 42, 1);
        Assert.Equal(InboxAttachmentDownloadStatus.NotFound, result.Status); Assert.Null(result.Content);
    }

    [Theory]
    [InlineData("C:\\private\\invoice.pdf", "invoice.pdf")]
    [InlineData("../../secret.txt", "secret.txt")]
    [InlineData("report\r\nInjected: yes.txt", "reportInjected yes.txt")]
    [InlineData("...", "attachment-1")]
    public void FilenamesAreSafeBasenames(string original, string expected)
    {
        using var message = new MimeMessage { Body = File(original, new MemoryStream([1])) };
        Assert.Equal(expected, Assert.Single(InboxAttachmentService.DescribeAttachments(message)).FileName);
    }

    [Fact]
    public async Task DecodedSizeLimitRejectsOversizeWithoutReturningPartialContent()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Imap.Message = new MimeMessage { Body = File("large.bin", new MemoryStream(new byte[InboxAttachmentService.MaxAttachmentBytes + 1])) };
        var result = await fixture.DownloadAsync();
        Assert.Equal(InboxAttachmentDownloadStatus.TooLarge, result.Status); Assert.Null(result.Content);
    }

    [Fact]
    public async Task CancellationIsNotConvertedToSuccessOrProviderFailure()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.DownloadAsync(cancellation.Token));
        Assert.Equal(0, fixture.Imap.FetchCount);
    }

    [Fact]
    public async Task CancellationDuringDecodePropagatesAndDoesNotReturnPartialDownload()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        fixture.Imap.Message = new MimeMessage { Body = File("cancel.bin", new CancelReadStream(cancellation)) };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.DownloadAsync(cancellation.Token));
        Assert.Equal(1, fixture.Imap.FetchCount);
    }

    private sealed class CancelReadStream(CancellationTokenSource cancellation) : MemoryStream(new byte[] { 1, 2, 3 })
    {
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            cancellation.Cancel(); return base.ReadAsync(buffer, offset, count, token);
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            cancellation.Cancel(); return base.ReadAsync(buffer, token);
        }
    }

    private static MimePart File(string name, Stream content) => new("application", "octet-stream") {
        ContentDisposition = new ContentDisposition("attachment") { FileName = name }, Content = new MimeContent(content) };
    private static ClaimsPrincipal Principal(string owner) => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, owner)], "test"));
    private sealed class UnreadableStream : MemoryStream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Metadata must not decode payloads");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Metadata must not decode payloads");
    }
    private sealed class Fixture(SqliteConnection connection, MailWinnowDbContext database, DestinationMailbox destination) : IAsyncDisposable
    {
        public MailWinnowDbContext Database { get; } = database;
        public DestinationMailbox Destination { get; } = destination;
        public FakeImap Imap { get; } = new();
        public LocalImapOptions Configuration { get; set; } = new() { Host = "imap.test", Port = 993, UseSsl = true };
        public string Identity => InboxAttachmentService.MailboxIdentity(Destination.Id, Destination.Username, Destination.Folder, Configuration);
        public InboxAttachmentService Service => new(Database, new OwnershipAuthorizer(), new Protector(), Options.Create(Configuration), Imap);
        public Task<InboxAttachmentDownloadResult> DownloadAsync(CancellationToken token = default) => Service.DownloadAsync(Principal("owner"), Destination.Id, "INBOX", Identity, 7, 42, 0, token);
        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
            var database = new MailWinnowDbContext(new DbContextOptionsBuilder<MailWinnowDbContext>().UseSqlite(connection).Options);
            await database.Database.EnsureCreatedAsync();
            var destination = new DestinationMailbox { OwnerUserId = "owner", Username = "owner-inbox", ProtectedCredential = "password", Folder = "INBOX" };
            database.DestinationMailboxes.Add(destination); await database.SaveChangesAsync();
            return new(connection, database, destination);
        }
        public async ValueTask DisposeAsync() { await Database.DisposeAsync(); await connection.DisposeAsync(); }
    }
    private sealed class Protector : ICredentialProtectionService
    {
        public string Protect(string value, CredentialKind kind) => value;
        public string Unprotect(string value, CredentialKind kind) => value;
    }
    private sealed class FakeImap : IImapConnectionService
    {
        public MimeMessage Message { get; set; } = new() { Body = new TextPart("plain") { Text = "No attachments" } };
        public int FetchCount { get; private set; }
        public ImapFailureKind Failure { get; set; }
        public (string Username, string Folder, uint Uid, uint Validity) Identity { get; private set; }
        public Task<ImapOperationResult<MimeMessage>> FetchMessageAsync(ImapConnectionSettings connection, string folder, uint uid, uint? validity = null, CancellationToken token = default)
        {
            FetchCount++; Identity = (connection.Username, folder, uid, validity ?? 0);
            return Task.FromResult(Failure == ImapFailureKind.None ? ImapOperationResult<MimeMessage>.Success(Message)
                : ImapOperationResult<MimeMessage>.Failure(Failure, "Private provider detail must not escape"));
        }
        public Task<ImapOperationResult<IReadOnlyList<ImapMessageHeader>>> FetchHeadersAsync(ImapConnectionSettings connection, string folder, IReadOnlyList<uint> uids, uint? validity = null, CancellationToken token = default) => Task.FromResult(ImapOperationResult<IReadOnlyList<ImapMessageHeader>>.Success([]));
        public Task<ImapOperationResult<ImapFolderSnapshot>> GetFolderSnapshotAsync(ImapConnectionSettings connection, string folder, CancellationToken token = default) => throw new NotSupportedException();
        public Task<ImapOperationResult<IReadOnlyList<string>>> ListFoldersAsync(ImapConnectionSettings connection, CancellationToken token = default) => throw new NotSupportedException();
        public Task<ImapOperationResult<uint?>> AppendMessageAsync(ImapConnectionSettings connection, string folder, MimeMessage message, CancellationToken token = default, DateTimeOffset? date = null) => throw new NotSupportedException();
        public Task<ImapOperationResult<int>> DeleteAndExpungeAsync(ImapConnectionSettings connection, string folder, IReadOnlyList<uint> uids, uint validity, CancellationToken token = default) => throw new NotSupportedException();
        public Task<ImapOperationResult<bool>> TestConnectionAsync(ImapConnectionSettings connection, CancellationToken token = default) => throw new NotSupportedException();
    }
}
