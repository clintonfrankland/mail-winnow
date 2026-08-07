using MailWinnow.Core.Rules;
using MailWinnow.Infrastructure.Mailboxes;
using MailWinnow.Infrastructure.Persistence;
using MailWinnow.Infrastructure.Rules;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using MailWinnow.Infrastructure.Security;

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

    [Theory]
    [InlineData(RuleAction.ApproveOneMessage)]
    [InlineData(RuleAction.PendingReview)]
    public async Task AddOrUpdate_RejectsMessageOnlyActions(RuleAction action)
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = new RuleManagementService(fixture.Db, new RuleEvaluationService(fixture.Db));

        await Assert.ThrowsAsync<ArgumentException>(() => service.AddOrUpdateAsync(new MailRule
        {
            OwnerUserId = "owner", Action = action, Scope = RuleScope.User,
            MatchType = RuleMatchType.ExactSender, MatchValue = "sender@example.test"
        }));
    }

    [Fact]
    public async Task AddOrUpdate_PersistsDefaultDeliveredMessageRetention()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = new RuleManagementService(fixture.Db, new RuleEvaluationService(fixture.Db));
        var rule = new MailRule
        {
            OwnerUserId = "owner", Action = RuleAction.PermanentlyAllow, Scope = RuleScope.User,
            MatchType = RuleMatchType.ExactSender, MatchValue = "sender@example.test"
        };

        await service.AddOrUpdateAsync(rule);

        var stored = await fixture.Db.MailRules.FindAsync(rule.Id);
        Assert.NotNull(stored);
        Assert.Null(stored.DeliveredMessageRetentionDays);
    }

    [Fact]
    public async Task AddOrUpdate_RejectsUnsupportedDestinationRetention()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = new RuleManagementService(fixture.Db, new RuleEvaluationService(fixture.Db));

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.AddOrUpdateAsync(new MailRule
        {
            OwnerUserId = "owner", Action = RuleAction.PermanentlyAllow, Scope = RuleScope.User,
            MatchType = RuleMatchType.ExactSender, MatchValue = "sender@example.test", DeliveredMessageRetentionDays = 2
        }));

        Assert.Contains("Destination retention", exception.Message);
    }

    [Fact]
    public async Task ReplacingAnotherUsersRule_IsRejectedAndDoesNotChangeIt()
    {
        await using var fixture = await Fixture.CreateAsync();
        var rule = new MailRule
        {
            OwnerUserId = "other", Action = RuleAction.PermanentlyAllow, Scope = RuleScope.User,
            MatchType = RuleMatchType.ExactSender, MatchValue = "sender@example.test"
        };
        fixture.Db.MailRules.Add(rule);
        await fixture.Db.SaveChangesAsync();
        var service = new RuleManagementService(fixture.Db, new RuleEvaluationService(fixture.Db));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReplaceAsync("owner", rule.Id, new MailRule
        {
            OwnerUserId = "owner", Action = RuleAction.PermanentlyBlock, Scope = RuleScope.User,
            MatchType = RuleMatchType.ExactSender, MatchValue = "sender@example.test"
        }));

        Assert.Equal("other", (await fixture.Db.MailRules.FindAsync(rule.Id))!.OwnerUserId);
    }

    [Fact]
    public async Task ReviewQueries_DoNotExposeAnotherUsersHeaders()
    {
        await using var fixture = await Fixture.CreateAsync();
        var own = await fixture.AddHeaderAsync("owner", "own@example.test", "Own");
        await fixture.AddHeaderAsync("other", "other@example.test", "Other");
        var review = new MessageReviewService(fixture.Db, new OwnershipAuthorizer());

        var result = await review.GetRecentAsync(Principal("owner"), new MessageReviewFilter(null, null, null));

        var item = Assert.Single(result);
        Assert.Equal(own.Id, item.Id);
        Assert.Equal("own@example.test", item.Sender);
    }

    [Fact]
    public async Task GroupedReviewQueriesIncludeOwnedMessageIdsAndDistinctSendersForActions()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.AddHeaderAsync("owner", "first@example.test", "Shared subject");
        var second = await fixture.AddHeaderAsync("owner", "second@example.test", "Shared subject");
        await fixture.AddHeaderAsync("other", "hidden@example.test", "Shared subject");
        var review = new MessageReviewService(fixture.Db, new OwnershipAuthorizer());

        var groups = await review.GetBySubjectAsync(Principal("owner"), new MessageReviewFilter(null, null, null));

        var group = Assert.Single(groups);
        Assert.Equal(new[] { first.Id, second.Id }.Order().ToArray(), group.MessageIds.Order().ToArray());
        Assert.Equal(["first@example.test", "second@example.test"], group.Senders);
    }

    [Fact]
    public async Task GroupedReviewQueriesSortLargestMessageCountFirst()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddHeaderAsync("owner", "small@example.test", "Small group");
        await fixture.AddHeaderAsync("owner", "large@example.test", "First");
        await fixture.AddHeaderAsync("owner", "large@example.test", "Second");
        await fixture.AddHeaderAsync("owner", "large@example.test", "Third");
        var review = new MessageReviewService(fixture.Db, new OwnershipAuthorizer());

        var groups = await review.GetBySenderAsync(Principal("owner"), new MessageReviewFilter(null, null, null));

        Assert.Equal([3, 1], groups.Select(x => x.Count));
        Assert.Equal(["large@example.test", "small@example.test"], groups.Select(x => x.Value));
    }

    [Theory]
    [InlineData(RuleAction.PermanentlyAllow)]
    [InlineData(RuleAction.PermanentlyBlock)]
    public async Task ReviewQueriesHideMessagesHandledBySenderOrDomainRules(RuleAction action)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddHeaderAsync("owner", "handled@example.test", "Handled by sender");
        await fixture.AddHeaderAsync("owner", "another@domain.test", "Handled by domain");
        var visible = await fixture.AddHeaderAsync("owner", "pending@elsewhere.test", "Needs review");
        fixture.Db.MailRules.AddRange(
            new MailRule { OwnerUserId = "owner", Action = action, Scope = RuleScope.User, MatchType = RuleMatchType.ExactSender, MatchValue = "handled@example.test" },
            new MailRule { OwnerUserId = "owner", Action = action, Scope = RuleScope.User, MatchType = RuleMatchType.SenderDomain, MatchValue = "domain.test" });
        await fixture.Db.SaveChangesAsync();
        var review = new MessageReviewService(fixture.Db, new OwnershipAuthorizer());

        var recent = await review.GetRecentAsync(Principal("owner"), new MessageReviewFilter(null, null, null));
        var senders = await review.GetBySenderAsync(Principal("owner"), new MessageReviewFilter(null, null, null));
        var subjects = await review.GetBySubjectAsync(Principal("owner"), new MessageReviewFilter(null, null, null));

        Assert.Equal(visible.Id, Assert.Single(recent).Id);
        Assert.Equal("pending@elsewhere.test", Assert.Single(senders).Value);
        Assert.Equal("Needs review", Assert.Single(subjects).Value);
    }

    [Fact]
    public async Task ReviewQueriesHideMessagesWithOneTimeApproval()
    {
        await using var fixture = await Fixture.CreateAsync();
        var handled = await fixture.AddHeaderAsync("owner", "approved@example.test", "Approved");
        var visible = await fixture.AddHeaderAsync("owner", "pending@example.test", "Pending");
        fixture.Db.MessageDecisions.Add(new MessageDecision
        {
            OwnerUserId = "owner", SourceMessageHeaderId = handled.Id, Action = RuleAction.ApproveOneMessage
        });
        await fixture.Db.SaveChangesAsync();
        var review = new MessageReviewService(fixture.Db, new OwnershipAuthorizer());

        var result = await review.GetRecentAsync(Principal("owner"), new MessageReviewFilter(null, null, null));

        Assert.Equal(visible.Id, Assert.Single(result).Id);
    }

    private static ClaimsPrincipal Principal(string userId) => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "Test"));

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
