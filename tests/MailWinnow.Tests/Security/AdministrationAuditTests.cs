using System.Security.Claims;
using MailWinnow.Infrastructure.Mailboxes;
using MailWinnow.Infrastructure.Persistence;
using MailWinnow.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MailWinnow.Tests.Security;

public sealed class AdministrationAuditTests
{
    [Fact]
    public async Task Operations_snapshot_requires_administrator_role()
    {
        await using var fixture = await Fixture.CreateAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.GetSnapshotAsync(Principal("member")));
    }

    [Fact]
    public async Task Audit_records_keep_only_operation_metadata()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string secret = "password=never-store-this; body=private attachment content";

        await fixture.Service.RecordAsync("delivery.failed", "administrator", "member", "delivery", "abc", secret);

        var audit = Assert.Single(fixture.Db.AuditEvents);
        Assert.Equal("delivery.failed", audit.EventType);
        Assert.Equal("abc", audit.ResourceId);
        Assert.Null(audit.Detail);
        Assert.DoesNotContain(secret, string.Join(' ', new[] { audit.EventType, audit.ResourceType, audit.ResourceId, audit.Detail }));
    }

    private static ClaimsPrincipal Principal(string id, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, id) };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        return new(new ClaimsIdentity(claims, "test"));
    }

    private sealed class Fixture(SqliteConnection connection, MailWinnowDbContext db) : IAsyncDisposable
    {
        public MailWinnowDbContext Db { get; } = db;
        public AdministrationService Service { get; } = new(db, Options.Create(new LocalImapOptions()), new FixedHealthChecker());

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new MailWinnowDbContext(new DbContextOptionsBuilder<MailWinnowDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            return new Fixture(connection, db);
        }

        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await connection.DisposeAsync(); }
    }

    private sealed class FixedHealthChecker : ILocalImapHealthChecker
    {
        public Task<string> CheckAsync(LocalImapOptions options, CancellationToken cancellationToken = default) => Task.FromResult("Reachable");
    }
}
