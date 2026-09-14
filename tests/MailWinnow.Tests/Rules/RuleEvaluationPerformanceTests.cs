using System.Diagnostics;
using MailWinnow.Core.Rules;
using MailWinnow.Infrastructure.Mailboxes;
using MailWinnow.Infrastructure.Persistence;
using MailWinnow.Infrastructure.Rules;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit.Abstractions;

namespace MailWinnow.Tests.Rules;

public sealed class RuleEvaluationPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public void CompiledRulesPreserveCanonicalPrecedenceAndNormalization()
    {
        var source = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var rules = Enumerable.Range(0, 751).Select(index => new RuleCandidate(Guid.NewGuid(),
            index % 3 == 0 ? RuleAction.PermanentlyBlock : RuleAction.PermanentlyAllow,
            index % 2 == 0 ? RuleScope.User : RuleScope.SourceAccount,
            (RuleMatchType)(index % 4), (index % 4) switch
            {
                0 => $"Person <sender{index % 20}@example.test>",
                1 => "@example.test>",
                2 => "  SPECIAL   Subject ",
                _ => "subject"
            }, source)).ToList();
        rules.Add(new RuleCandidate(Guid.NewGuid(), RuleAction.TemporarilyAllow, RuleScope.User,
            RuleMatchType.ExactSender, "expired@other.test", null, now.AddDays(-2), now.AddDays(-1)));
        foreach (var value in new[] { "@", ">", " ", "@>" })
            rules.Add(new RuleCandidate(Guid.NewGuid(), RuleAction.PermanentlyBlock, RuleScope.User,
                RuleMatchType.SenderDomain, value, null));
        var compiled = RuleEvaluator.Compile(rules);
        foreach (var sender in new[] { "Sender0@EXAMPLE.test", "Display <sender4@example.test>", "bad", "", "expired@other.test", "a@example.test,b@example.test" })
        foreach (var subject in new[] { "Special subject", "other", "  SPECIAL    Subject " })
        foreach (var mailbox in new[] { source, Guid.NewGuid() })
        foreach (var decision in new RuleAction?[] { null, RuleAction.ApproveOneMessage, RuleAction.PendingReview, RuleAction.DeleteOneMessage })
        {
            var expected = RuleEvaluator.Evaluate(rules, sender, subject, mailbox, now, decision);
            var actual = compiled.Evaluate(sender, subject, mailbox, now, decision);
            Assert.Equal(expected.Outcome, actual.Outcome);
            Assert.Equal(expected.AppliedRule, actual.AppliedRule);
            Assert.Equal(expected.ConflictingRules, actual.ConflictingRules);
        }
    }

    [Fact]
    public async Task LargeCatalogueUsesBoundedTrackerAndDoesNotResaveExistingDeliveries()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var tracker = new TrackerObserver();
        await using var database = new MailWinnowDbContext(new DbContextOptionsBuilder<MailWinnowDbContext>()
            .UseSqlite(connection).AddInterceptors(tracker).Options);
        await database.Database.EnsureCreatedAsync();
        var source = new SourceMailbox { OwnerUserId = "owner", DisplayName = "Mailbox", Host = "imap.test",
            Port = 993, Username = "owner", ProtectedCredential = "protected" };
        database.SourceMailboxes.Add(source);
        var headers = Enumerable.Range(1, 4456).Select(index => new SourceMessageHeader
        {
            SourceMailboxId = source.Id, FolderName = "INBOX", UidValidity = 1, Uid = (uint)index,
            From = $"sender{index % 750}@example.test", Subject = "Message", ReceivedUtc = DateTimeOffset.UtcNow
        }).ToArray();
        database.SourceMessageHeaders.AddRange(headers);
        database.MailRules.AddRange(Enumerable.Range(0, 750).Select(index => new MailRule
        {
            OwnerUserId = "owner", Action = RuleAction.PermanentlyAllow, Scope = RuleScope.User,
            MatchType = RuleMatchType.ExactSender, MatchValue = $"sender{index}@example.test"
        }));
        database.MailRules.Add(new MailRule { OwnerUserId = "someone-else", Action = RuleAction.PermanentlyBlock,
            Scope = RuleScope.User, MatchType = RuleMatchType.SenderDomain, MatchValue = "example.test" });
        database.MessageDeliveries.AddRange(headers.Select(header => new MessageDelivery
            { OwnerUserId = "owner", SourceMessageHeaderId = header.Id, State = MessageDeliveryState.Delivered }));
        await database.SaveChangesAsync();
        database.ChangeTracker.Clear();
        var retained = new ReviewDecisionWorkItem { OwnerUserId = "owner", MatchValue = "example.test",
            MessageIdsJson = "[]", IdempotencyKey = "retained", Status = ReviewDecisionWorkStatus.Processing };
        database.ReviewDecisionWorkItems.Add(retained);
        await database.SaveChangesAsync();
        tracker.Observe = true;
        var deliveries = new NoExistingDeliveryWrites();
        var evaluator = new RuleEvaluationService(database, deliveries);
        var stopwatch = Stopwatch.StartNew();
        await evaluator.ReevaluateOwnedHeadersAsync("owner");
        stopwatch.Stop();
        output.WriteLine($"4456 messages/751 rules catalogue pass: {stopwatch.Elapsed.TotalMilliseconds:F0}ms; max tracked headers: {tracker.MaximumHeaders}");
        Assert.Equal(4456, await database.SourceMessageHeaders.CountAsync(header => header.EvaluationOutcome == RuleOutcome.Allow));
        Assert.InRange(tracker.MaximumHeaders, 1, 200);
        Assert.Empty(database.ChangeTracker.Entries<SourceMessageHeader>());
        Assert.Equal(EntityState.Unchanged, database.Entry(retained).State);
        Assert.Equal(0, deliveries.Calls);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), "Synthetic catalogue pass exceeded the documented 20-second bound.");
        var timestamps = await database.SourceMessageHeaders.AsNoTracking().Select(header => header.EvaluatedUtc).ToArrayAsync();
        await evaluator.ReevaluateOwnedHeadersAsync("owner");
        Assert.Equal(timestamps, await database.SourceMessageHeaders.AsNoTracking().Select(header => header.EvaluatedUtc).ToArrayAsync());
        retained.Status = ReviewDecisionWorkStatus.Completed;
        await database.SaveChangesAsync();
        Assert.Equal(ReviewDecisionWorkStatus.Completed, (await database.ReviewDecisionWorkItems.AsNoTracking().SingleAsync()).Status);
    }

    private sealed class TrackerObserver : SaveChangesInterceptor
    {
        public bool Observe { get; set; }
        public int MaximumHeaders { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Observe) MaximumHeaders = Math.Max(MaximumHeaders, eventData.Context!.ChangeTracker.Entries<SourceMessageHeader>().Count());
            return ValueTask.FromResult(result);
        }
    }

    private sealed class NoExistingDeliveryWrites : IMessageDeliveryService
    {
        public int Calls { get; private set; }
        public Task QueueApprovedAsync(string ownerUserId, Guid headerId, Guid? approvalRuleId = null, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Existing deliveries must not be queued again."); }
        public Task DeliverAsync(Guid deliveryId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Guid>> GetDueDeliveryIdsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Guid>> GetDueCleanupIdsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CleanupExpiredAsync(Guid deliveryId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailboxOperationResult> RetryAsync(System.Security.Claims.ClaimsPrincipal actor, Guid deliveryId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
