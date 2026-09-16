using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using MailWinnow.Infrastructure.Mailboxes;
using MailWinnow.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MailWinnow.Tests.Mailboxes;

public sealed class InboxAttachmentEndpointTests
{
    private static readonly Guid MailboxId = Guid.NewGuid();
    private static string Path(string uid = "7", string validity = "42", string index = "0") =>
        $"/inbox/attachments/{MailboxId}/{uid}/{validity}/{index}?folder=Inbox%2FReceipts&identity=test-stamp";

    [Fact]
    public async Task AnonymousRequestCannotFetchAttachment()
    {
        var service = new RecordingAttachments();
        await using var app = CreateApp(service);
        await app.StartAsync();
        using var client = app.GetTestClient();
        var response = await client.GetAsync(Path());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, service.Calls);
    }

    [Fact]
    public async Task AuthenticatedDownloadStreamsBytesAsPrivateAttachmentWithExactMessageIdentity()
    {
        var service = new RecordingAttachments();
        await using var app = CreateApp(service);
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-User", "owner-a");
        var response = await client.GetAsync(Path());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new byte[] { 0, 1, 2, 255 }, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("invoice.pdf", response.Content.Headers.ContentDisposition?.FileNameStar);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Contains("nosniff", response.Headers.GetValues("X-Content-Type-Options"));
        Assert.Contains("default-src 'none'; sandbox", response.Headers.GetValues("Content-Security-Policy"));
        Assert.Equal(("owner-a", MailboxId, "Inbox/Receipts", "test-stamp", 7u, 42u, 0), service.Identity);
    }

    [Theory]
    [InlineData("0", "42", "0")]
    [InlineData("4294967296", "42", "0")]
    [InlineData("7", "0", "0")]
    [InlineData("7", "42", "-1")]
    public async Task InvalidIdentityDoesNotFetchMail(string uid, string validity, string index)
    {
        var service = new RecordingAttachments();
        await using var app = CreateApp(service);
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-User", "owner");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Path(uid, validity, index))).StatusCode);
        Assert.Equal(0, service.Calls);
    }

    [Theory]
    [InlineData(InboxAttachmentDownloadStatus.NotFound, 404)]
    [InlineData(InboxAttachmentDownloadStatus.TooLarge, 413)]
    [InlineData(InboxAttachmentDownloadStatus.Unavailable, 503)]
    public async Task FailureIsNotServedAsAFileOrCached(InboxAttachmentDownloadStatus status, int expected)
    {
        var service = new RecordingAttachments { Status = status };
        await using var app = CreateApp(service);
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-User", "owner");
        var response = await client.GetAsync(Path());
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.Null(response.Content.Headers.ContentDisposition);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    private static WebApplication CreateApp(RecordingAttachments service)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IInboxAttachmentService>(service);
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuthentication>("test", _ => { });
        builder.Services.AddAuthorization();
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapInboxAttachmentEndpoints();
        return app;
    }

    private sealed class RecordingAttachments : IInboxAttachmentService
    {
        public int Calls;
        public InboxAttachmentDownloadStatus Status = InboxAttachmentDownloadStatus.Success;
        public (string?, Guid, string, string, uint, uint, int) Identity;
        public Task<InboxAttachmentDownloadResult> DownloadAsync(ClaimsPrincipal user, Guid destinationMailboxId,
            string destinationFolder, string mailboxIdentity, uint uid, uint uidValidity, int attachmentIndex, CancellationToken cancellationToken = default)
        {
            Calls++;
            Identity = (user.FindFirstValue(ClaimTypes.NameIdentifier), destinationMailboxId, destinationFolder, mailboxIdentity, uid, uidValidity, attachmentIndex);
            return Task.FromResult(Status == InboxAttachmentDownloadStatus.Success
                ? new InboxAttachmentDownloadResult(Status, new MemoryStream([0, 1, 2, 255]), "invoice.pdf")
                : new InboxAttachmentDownloadResult(Status));
        }
    }

    private sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(
            Request.Headers.TryGetValue("X-Test-User", out var owner)
                ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, owner.ToString())], Scheme.Name)), Scheme.Name))
                : AuthenticateResult.NoResult());
    }
}
