using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using MailWinnow.Core.Rules;
using MailWinnow.Infrastructure.Mailboxes;
using MailWinnow.Infrastructure.Persistence;
using MailWinnow.Infrastructure.Rules;
using MailWinnow.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MailWinnow.Tests.Review;

public sealed class NavigationCountProjectionTests
{
    [Fact]
    public void ProductionSqlUsesAggregatesOwnerPredicatesAndAccentSensitiveSenderGrouping()
    {
        using var database = new MailWinnowDbContext(new DbContextOptionsBuilder<MailWinnowDbContext>()
            .UseSqlServer("Server=localhost;Database=ProjectionTranslationOnly;Integrated Security=true").Options);
        var sql = new NavigationCountService(database, new OwnershipAuthorizer()).BuildReviewCountQuery("owner").ToQueryString();
        Assert.Contains("COUNT", sql);
        Assert.Contains("DISTINCT", sql);
        Assert.Contains("Latin1_General_100_BIN2", sql);
        Assert.Contains("OwnerUserId", sql);
        Assert.Contains("NOT EXISTS", sql);
        Assert.DoesNotContain("[Subject]", sql);
        Assert.DoesNotContain("MailRules", sql);
    }

    [Fact]
    public async Task LargeOwnerScopedCountsUseScalarSqlAndExcludeAcceptedCommandsWithoutLoadingCatalogue()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var commands = new CommandCapture();
        await using var database = new MailWinnowDbContext(new DbContextOptionsBuilder<MailWinnowDbContext>()
            .UseSqlite(connection).AddInterceptors(commands).Options);
        await database.Database.EnsureCreatedAsync();
        var own = Source("owner");
        var other = Source("other");
        database.SourceMailboxes.AddRange(own, other);
        var headers = Enumerable.Range(1, 1500).Select(index => new SourceMessageHeader
        {
            SourceMailboxId = own.Id, FolderName = "INBOX", Uid = (uint)index, UidValidity = 1,
            From = index % 2 == 0 ? "Sender@example.test" : "sender@example.test", Subject = "Private subject",
            EvaluationOutcome = index <= 1400 ? RuleOutcome.Pending : RuleOutcome.Allow
        }).ToArray();
        database.SourceMessageHeaders.AddRange(headers);
        database.SourceMessageHeaders.Add(new() { SourceMailboxId = other.Id, FolderName = "INBOX", Uid = 1, UidValidity = 1, From = "other@example.test" });
        database.ReviewDecisionWorkItems.AddRange(
            Work("owner", ReviewDecisionWorkStatus.Pending, headers[0].Id),
            Work("owner", ReviewDecisionWorkStatus.Processing, headers[1].Id, uppercase: true),
            Work("owner", ReviewDecisionWorkStatus.Retrying, headers[2].Id),
            Work("owner", ReviewDecisionWorkStatus.Failed, headers[3].Id),
            Work("owner", ReviewDecisionWorkStatus.Completed, headers[4].Id),
            Work("other", ReviewDecisionWorkStatus.Pending, headers[5].Id));
        await database.SaveChangesAsync();
        // Outcomes are persisted by the evaluator after catalogue insertion. EF's existing
        // enum-zero insert sentinel otherwise substitutes the column's Pending default for Allow.
        await database.SourceMessageHeaders.Where(header => header.SourceMailboxId == own.Id && header.Uid > 1400)
            .ExecuteUpdateAsync(update => update.SetProperty(header => header.EvaluationOutcome, RuleOutcome.Allow));
        database.ChangeTracker.Clear();
        commands.Sql.Clear();

        var result = await new NavigationCountService(database, new OwnershipAuthorizer()).GetReviewCountsAsync(User("owner"));

        Assert.Equal(new ReviewNavigationCounts(1397, 1), result);
        Assert.Empty(database.ChangeTracker.Entries());
        var sql = Assert.Single(commands.Sql);
        Assert.Contains("COUNT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GROUP BY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"Subject\"", sql);
        Assert.DoesNotContain("MailRules", sql);
        Assert.DoesNotContain("MessageDeliveries", sql);
    }

    [Fact]
    public async Task InboxUsesOwnerSnapshotAndPreservesUnknownVersusKnownZero()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var database = new MailWinnowDbContext(new DbContextOptionsBuilder<MailWinnowDbContext>().UseSqlite(connection).Options);
        await database.Database.EnsureCreatedAsync();
        database.DestinationMailboxes.AddRange(
            new() { OwnerUserId = "owner", Username = "owner", Folder = "INBOX", ProtectedCredential = "protected", InboxMessageCount = 0 },
            new() { OwnerUserId = "other", Username = "other", Folder = "INBOX", ProtectedCredential = "protected", InboxMessageCount = 500 },
            new() { OwnerUserId = "unobserved", Username = "unobserved", Folder = "INBOX", ProtectedCredential = "protected" });
        await database.SaveChangesAsync();
        var counts = new NavigationCountService(database, new OwnershipAuthorizer());
        Assert.Equal(0, await counts.GetInboxCountAsync(User("owner")));
        Assert.Null(await counts.GetInboxCountAsync(User("unobserved")));
        Assert.Null(await counts.GetInboxCountAsync(User("unknown")));
        Assert.Equal(new ReviewNavigationCounts(0, 0), await counts.GetReviewCountsAsync(User("unknown")));
    }

    private static SourceMailbox Source(string owner) => new() { OwnerUserId = owner, DisplayName = owner, Host = "imap.test", Port = 993, Username = owner, ProtectedCredential = "protected" };
    private static ClaimsPrincipal User(string owner) => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, owner)], "test"));
    private static ReviewDecisionWorkItem Work(string owner, ReviewDecisionWorkStatus status, Guid message, bool uppercase = false) => new()
    {
        OwnerUserId = owner, Status = status, MatchValue = "sender@example.test", MatchType = RuleMatchType.ExactSender,
        Action = RuleAction.PermanentlyAllow, IdempotencyKey = Guid.NewGuid().ToString(), MessageIdsJson = uppercase ? JsonSerializer.Serialize(new[] { message }).ToUpperInvariant() : JsonSerializer.Serialize(new[] { message })
    };

    private sealed class CommandCapture : DbCommandInterceptor
    {
        public List<string> Sql { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Sql.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
