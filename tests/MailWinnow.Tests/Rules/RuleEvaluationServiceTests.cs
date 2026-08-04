using MailWinnow.Core.Rules;
using MailWinnow.Infrastructure.Mailboxes;
using MailWinnow.Infrastructure.Persistence;
using MailWinnow.Infrastructure.Rules;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MailWinnow.Tests.Rules;

public sealed class RuleEvaluationServiceTests
{
    [Fact]
    public async Task AddingRule_ReevaluatesOnlyTheOwnersPendingHeaders()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ownHeader = await fixture.AddHeaderAsync("owner", "offers@example.test", "Sale");
        var otherHeader = await fixture.AddHeaderAsync("other", "offers@example.test", "Sale");
        var evaluator = new RuleEvaluationService(fixture.Db);
        var service = new RuleManagementService(fixture.Db, evaluator);

        await service.AddOrUpdateAsync(new MailRule
        {
            OwnerUserId = "owner", Action = RuleAction.PermanentlyAllow, Scope = RuleScope.User,
            MatchType = RuleMatchType.SenderDomain, MatchValue = "example.test", DeliveredMessageRetentionDays = 30
        });

        Assert.Equal(RuleOutcome.Allow, (await fixture.Db.SourceMessageHeaders.FindAsync(ownHeader.Id))!.EvaluationOutcome);
        Assert.Equal(RuleOutcome.Pending, (await fixture.Db.SourceMessageHeaders.FindAsync(otherHeader.Id))!.EvaluationOutcome);
    }

    [Fact]
    public async Task MessageDecision_CannotCrossOwnershipBoundary()
    {
        await using var fixture = await Fixture.CreateAsync();
        var otherHeader = await fixture.AddHeaderAsync("other", "offers@example.test", "Sale");
        var service = new RuleManagementService(fixture.Db, new RuleEvaluationService(fixture.Db));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetMessageDecisionAsync(new MessageDecision
        {
            OwnerUserId = "owner", SourceMessageHeaderId = otherHeader.Id, Action = RuleAction.ApproveOneMessage
        }));
    }

    private sealed class Fixture(SqliteConnection connection, MailWinnowDbContext db) : IAsyncDisposable
    {
        public MailWinnowDbContext Db { get; } = db;
        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new MailWinnowDbContext(new DbContextOptionsBuilder<MailWinnowDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            return new Fixture(connection, db);
        }

        public async Task<SourceMessageHeader> AddHeaderAsync(string owner, string from, string subject)
        {
            var source = new SourceMailbox { OwnerUserId = owner, DisplayName = owner, Host = "imap.test", Port = 993, Username = owner + "@test", ProtectedCredential = "protected" };
            Db.SourceMailboxes.Add(source);
            var header = new SourceMessageHeader { SourceMailboxId = source.Id, FolderName = "INBOX", UidValidity = 1, Uid = (uint)(await Db.SourceMessageHeaders.CountAsync() + 1), From = from, Subject = subject, ReceivedUtc = DateTimeOffset.UtcNow };
            Db.SourceMessageHeaders.Add(header);
            await Db.SaveChangesAsync();
            return header;
        }

        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await connection.DisposeAsync(); }
    }
}
