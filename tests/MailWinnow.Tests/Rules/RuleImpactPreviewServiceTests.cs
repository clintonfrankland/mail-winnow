using MailWinnow.Core.Rules;
using MailWinnow.Infrastructure.Mailboxes;
using MailWinnow.Infrastructure.Persistence;
using MailWinnow.Infrastructure.Rules;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace MailWinnow.Tests.Rules;

public sealed class RuleImpactPreviewServiceTests
{
    [Fact]
    public async Task Preview_IsReadOnlyOwnerBoundAndSamplesNewestFiveHeadersOnly()
    {
        await using var f = await Fixture.CreateAsync();
        for (var i = 0; i < 7; i++) await f.AddHeaderAsync("owner", "sender@example.test", i == 6 ? null : $"Subject {i}", f.Now.AddMinutes(i));
        await f.AddHeaderAsync("other", "sender@example.test", "Private", f.Now.AddHours(1));
        var before = await f.Db.MailRules.CountAsync();

        var result = await new RuleImpactPreviewService(f.Db).PreviewAsync("owner", Rule("owner", RuleAction.PermanentlyAllow), null, f.Now);

        Assert.Equal(7, result.AllowCount); Assert.Equal(0, result.BlockCount); Assert.Equal(5, result.Samples.Count); Assert.Equal(2, result.OmittedCount);
        Assert.Equal("(no subject)", result.Samples[0].Subject);
        Assert.DoesNotContain(result.Samples, x => x.Subject == "Private");
        Assert.Equal(before, await f.Db.MailRules.CountAsync());
        Assert.All(await f.Db.SourceMessageHeaders.ToListAsync(), x => Assert.Equal(RuleOutcome.Pending, x.EvaluationOutcome));
        Assert.Empty(await f.Db.MessageDeliveries.ToListAsync());
    }

    [Fact]
    public async Task Preview_ReplacementAndProductionPrecedenceAreApplied()
    {
        await using var f = await Fixture.CreateAsync();
        var header = await f.AddHeaderAsync("owner", "sender@example.test", "Subject", f.Now);
        var old = Rule("owner", RuleAction.PermanentlyAllow); f.Db.MailRules.Add(old);
        var accountBlock = Rule("owner", RuleAction.PermanentlyBlock); accountBlock.Scope = RuleScope.SourceAccount; accountBlock.SourceMailboxId = header.SourceMailboxId;
        f.Db.MailRules.Add(accountBlock); await f.Db.SaveChangesAsync();
        var replacement = Rule("owner", RuleAction.PermanentlyAllow); replacement.MatchValue = "nobody@example.test";

        var result = await new RuleImpactPreviewService(f.Db).PreviewAsync("owner", replacement, old.Id, f.Now);

        Assert.Equal(0, result.AllowCount); Assert.Equal(1, result.BlockCount);
    }

    [Fact]
    public async Task Preview_RespectsTemporaryWindowAndSourceScope()
    {
        await using var f = await Fixture.CreateAsync();
        var own = await f.AddHeaderAsync("owner", "sender@example.test", "Subject", f.Now);
        await f.AddHeaderAsync("owner", "sender@example.test", "Other account", f.Now, newMailbox: true);
        var proposal = Rule("owner", RuleAction.TemporarilyAllow); proposal.Scope = RuleScope.SourceAccount; proposal.SourceMailboxId = own.SourceMailboxId;
        proposal.EffectiveUtc = f.Now; proposal.ExpiresUtc = f.Now.AddMinutes(5);
        var service = new RuleImpactPreviewService(f.Db);

        Assert.Equal(1, (await service.PreviewAsync("owner", proposal, null, f.Now)).AllowCount);
        Assert.Equal(0, (await service.PreviewAsync("owner", proposal, null, f.Now.AddMinutes(6))).AffectedCount);
    }

    [Fact]
    public async Task Preview_BlockZeroMatchesAndHeaderOnlyFallbacksAreDeterministic()
    {
        await using var f = await Fixture.CreateAsync();
        await f.AddHeaderAsync("owner", null, "match this", f.Now.AddMinutes(2));
        await f.AddHeaderAsync("owner", "sender@example.test", null, f.Now.AddMinutes(1));
        var service = new RuleImpactPreviewService(f.Db);

        var block = await service.PreviewAsync("owner", Rule("owner", RuleAction.PermanentlyBlock), null, f.Now);
        var unknownSenderRule = Rule("owner", RuleAction.PermanentlyBlock);
        unknownSenderRule.MatchType = RuleMatchType.SubjectContains;
        unknownSenderRule.MatchValue = "match";
        var unknownSender = await service.PreviewAsync("owner", unknownSenderRule, null, f.Now);
        var zeroRule = Rule("owner", RuleAction.PermanentlyBlock);
        zeroRule.MatchValue = "nobody@example.test";
        var zero = await service.PreviewAsync("owner", zeroRule, null, f.Now);

        Assert.Equal(0, zero.AffectedCount);
        Assert.Equal(1, block.BlockCount);
        Assert.Equal("(no subject)", block.Samples.Single().Subject);
        Assert.Equal("(unknown sender)", unknownSender.Samples.Single().Sender);
        Assert.DoesNotContain("Body", JsonSerializer.Serialize(block));
    }

    private static MailRule Rule(string owner, RuleAction action) => new() { OwnerUserId = owner, Action = action, Scope = RuleScope.User, MatchType = RuleMatchType.ExactSender, MatchValue = "sender@example.test" };

    private sealed class Fixture(SqliteConnection connection, MailWinnowDbContext db) : IAsyncDisposable
    {
        public MailWinnowDbContext Db { get; } = db; public DateTimeOffset Now { get; } = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        public static async Task<Fixture> CreateAsync() { var c = new SqliteConnection("Data Source=:memory:"); await c.OpenAsync(); var db = new MailWinnowDbContext(new DbContextOptionsBuilder<MailWinnowDbContext>().UseSqlite(c).Options); await db.Database.EnsureCreatedAsync(); return new(c, db); }
        public async Task<SourceMessageHeader> AddHeaderAsync(string owner, string? from, string? subject, DateTimeOffset received, bool newMailbox = true)
        {
            var source = !newMailbox ? await Db.SourceMailboxes.FirstAsync(x => x.OwnerUserId == owner) : new SourceMailbox { OwnerUserId = owner, DisplayName = owner, Host = "imap.test", Port = 993, Username = owner, ProtectedCredential = "x" };
            if (newMailbox) Db.SourceMailboxes.Add(source);
            var h = new SourceMessageHeader { SourceMailboxId = source.Id, FolderName = "INBOX", UidValidity = 1, Uid = (uint)(await Db.SourceMessageHeaders.CountAsync() + 1), From = from, Subject = subject, ReceivedUtc = received };
            Db.SourceMessageHeaders.Add(h); await Db.SaveChangesAsync(); return h;
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await connection.DisposeAsync(); }
    }
}
