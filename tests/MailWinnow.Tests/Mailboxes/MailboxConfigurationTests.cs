using System.Security.Claims;
using MailWinnow.Infrastructure.Mailboxes;
using MailWinnow.Infrastructure.Persistence;
using MailWinnow.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MailWinnow.Web.Security;

namespace MailWinnow.Tests.Mailboxes;

public sealed class MailboxConfigurationTests
{
    [Fact]
    public async Task SourceAndDestinationAreOwnerScopedAndCredentialsAreNotReturned()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MailWinnowDbContext>().UseSqlite(connection).Options;
        await using var db = new MailWinnowDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var service = new MailboxConfigurationService(db, new OwnershipAuthorizer(), new TestProtector(),
            Options.Create(new LocalImapOptions { Host = "local-imap", Port = 993, UseSsl = true }), new ImapConnectionService());
        var owner = Principal("owner");

        var saved = await service.SaveSourceAsync(owner, null, new(
            "Personal", "imap.example.test", 993, true, "owner@example.test", "secret-password", true, ["INBOX"]));
        Assert.True(saved.Succeeded, saved.Message);
        var source = Assert.Single(await service.ListSourcesAsync(owner));
        Assert.Equal("Personal", source.DisplayName);
        Assert.Equal(["INBOX"], source.SelectedFolders);
        Assert.DoesNotContain("secret-password", source.ToString(), StringComparison.Ordinal);
        Assert.Contains("sealed:secret-password", (await db.SourceMailboxes.SingleAsync()).ProtectedCredential, StringComparison.Ordinal);

        var updated = await service.SaveSourceAsync(owner, source.Id, new(
            "Renamed", "imap.example.test", 993, true, "owner@example.test", null, false, ["Archive"]));
        Assert.True(updated.Succeeded, updated.Message);
        source = Assert.Single(await service.ListSourcesAsync(owner));
        Assert.Equal("Renamed", source.DisplayName);
        Assert.False(source.Enabled);
        Assert.Equal(["Archive"], source.SelectedFolders);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SetSourceEnabledAsync(Principal("other"), source.Id, false));

        var destination = await service.SaveDestinationAsync(owner, new("owner-local", "destination-secret", "Approved", true));
        Assert.True(destination.Succeeded, destination.Message);
        Assert.Equal(new DestinationMailboxSummary("owner-local", "Approved", true), await service.GetDestinationAsync(owner));
        Assert.Contains("sealed:destination-secret", (await db.DestinationMailboxes.SingleAsync()).ProtectedCredential, StringComparison.Ordinal);
        Assert.Null(await service.GetDestinationAsync(Principal("other")));
    }

    [Fact]
    public void LocalImapInvalidCertificateDefaultsToFalse()
    {
        Assert.False(new LocalImapOptions().AllowInvalidCertificate);
    }

    [Fact]
    public async Task DiscoveredFoldersCanBeSelectedAndSavedByTheirOwner()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MailWinnowDbContext>().UseSqlite(connection).Options;
        await using var db = new MailWinnowDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var service = new MailboxConfigurationService(db, new OwnershipAuthorizer(), new TestProtector(), Options.Create(new LocalImapOptions()), new ImapConnectionService());
        var owner = Principal("owner");
        await service.SaveSourceAsync(owner, null, new("Personal", "imap.example.test", 993, true, "owner@example.test", "secret", true, ["INBOX"]));
        var source = Assert.Single(await service.ListSourcesAsync(owner));

        var saved = await service.SaveSourceFoldersAsync(owner, source.Id, ["Archive", "INBOX", "Archive"]);

        Assert.True(saved.Succeeded, saved.Message);
        Assert.Equal(["Archive", "INBOX"], Assert.Single(await service.ListSourcesAsync(owner)).SelectedFolders);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveSourceFoldersAsync(Principal("other"), source.Id, ["Inbox"]));
    }

    [Fact]
    public void DiscoveryRedirectCarriesSourceAndFoldersToTheMailboxesPage()
    {
        var sourceId = Guid.Parse("b9965995-77f0-4699-9ce9-5c6b4b892e83");

        var url = MailboxEndpoints.BuildRedirectUrl(new(true, "Connection succeeded; folders discovered.", ["INBOX", "Family & Friends"]), sourceId);

        Assert.Equal("/mailboxes?saved=Connection%20succeeded%3B%20folders%20discovered.&discoveredSourceId=b9965995-77f0-4699-9ce9-5c6b4b892e83&discoveredFolder=INBOX&discoveredFolder=Family%20%26%20Friends", url);
    }

    private static ClaimsPrincipal Principal(string id) => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "Test"));
    private sealed class TestProtector : ICredentialProtectionService
    {
        public string Protect(string credential, CredentialKind kind) => "sealed:" + credential;
        public string Unprotect(string protectedCredential, CredentialKind kind) => protectedCredential[7..];
    }
}
