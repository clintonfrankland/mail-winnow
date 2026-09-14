using MailWinnow.Core.Rules;
using MailWinnow.Infrastructure.Mailboxes;
using MailWinnow.Infrastructure.Persistence;
using MailWinnow.Infrastructure.Rules;
using MailWinnow.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;

namespace MailWinnow.Tests.Review;

public sealed class NavigationProjectionRefreshTests
{
    [Fact]
    public async Task SnapshotRefreshRetainsLastGoodCountOnFailureAndRejectsChangedDestinationIdentity()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var database = new MailWinnowDbContext(new DbContextOptionsBuilder<MailWinnowDbContext>().UseSqlite(connection).Options);
        await database.Database.EnsureCreatedAsync();
        var destination = new DestinationMailbox { OwnerUserId = "owner", Username = "owner", Folder = "INBOX", ProtectedCredential = "protected" };
        database.DestinationMailboxes.Add(destination);
        await database.SaveChangesAsync();
        var imap = new SnapshotImap();
        var service = new NavigationProjectionRefreshService(database, imap, new Protector(), Options.Create(new LocalImapOptions()));

        await service.RefreshInboxAsync(destination.Id, default);
        var observed = await database.DestinationMailboxes.AsNoTracking().SingleAsync();
        Assert.Equal(3, observed.InboxMessageCount);
        Assert.NotNull(observed.InboxCountObservedUtc);
        imap.Fail = true;
        await service.RefreshInboxAsync(destination.Id, default);
        Assert.Equal(observed.InboxCountObservedUtc, (await database.DestinationMailboxes.AsNoTracking().SingleAsync()).InboxCountObservedUtc);

        imap.Fail = false;
        imap.BeforeSnapshot = () => database.DestinationMailboxes.ExecuteUpdateAsync(update => update.SetProperty(x => x.Folder, "Other"));
        await service.RefreshInboxAsync(destination.Id, default);
        Assert.Equal(observed.InboxCountObservedUtc, (await database.DestinationMailboxes.AsNoTracking().SingleAsync()).InboxCountObservedUtc);
    }

    [Fact]
    public async Task TemporalRulesRefreshOnceAtStartupAndAfterBoundaryChangeNotAtEveryCountPoll()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MailWinnowDbContext>().UseSqlite(connection).Options;
        await using var database = new MailWinnowDbContext(options);
        await database.Database.EnsureCreatedAsync();
        var temporary = new MailRule { OwnerUserId = "owner", MatchValue = "sender@test", Action = RuleAction.TemporarilyAllow,
            MatchType = RuleMatchType.ExactSender, Scope = RuleScope.User, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1) };
        database.MailRules.AddRange(temporary, new() { OwnerUserId = "other", MatchValue = "other@test", Action = RuleAction.PermanentlyAllow });
        await database.SaveChangesAsync();
        var evaluator = new CountingEvaluator();
        var registrations = new ServiceCollection();
        registrations.AddScoped(_ => new MailWinnowDbContext(options));
        registrations.AddSingleton<IRuleEvaluationService>(evaluator);
        await using var services = registrations.BuildServiceProvider();
        var worker = new NavigationProjectionRefreshWorker(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<NavigationProjectionRefreshWorker>.Instance);

        await worker.RefreshTemporaryRulesAsync(default);
        await worker.RefreshTemporaryRulesAsync(default);
        Assert.Equal(["owner"], evaluator.Owners);
        temporary.ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        await database.SaveChangesAsync();
        await worker.RefreshTemporaryRulesAsync(default);
        await worker.RefreshTemporaryRulesAsync(default);
        Assert.Equal(["owner", "owner"], evaluator.Owners);
        Assert.Empty(database.ChangeTracker.Entries<SourceMessageHeader>());
    }

    private sealed class CountingEvaluator : IRuleEvaluationService
    {
        public List<string> Owners { get; } = [];
        public Task ReevaluateOwnedHeadersAsync(string ownerUserId, CancellationToken cancellationToken = default) { Owners.Add(ownerUserId); return Task.CompletedTask; }
        public Task<RuleEvaluation> EvaluateAsync(string ownerUserId, Guid headerId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReevaluatePendingHeadersAsync(string ownerUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Protector : ICredentialProtectionService
    {
        public string Protect(string value, CredentialKind kind) => value;
        public string Unprotect(string value, CredentialKind kind) => value;
    }

    private sealed class SnapshotImap : IImapConnectionService
    {
        public bool Fail { get; set; }
        public Func<Task>? BeforeSnapshot { get; set; }
        public async Task<ImapOperationResult<ImapFolderSnapshot>> GetFolderSnapshotAsync(ImapConnectionSettings connection, string folder, CancellationToken token = default)
        {
            if (BeforeSnapshot is not null) await BeforeSnapshot();
            return Fail ? new ImapOperationResult<ImapFolderSnapshot>(false, null, Error: "Unavailable") : ImapOperationResult<ImapFolderSnapshot>.Success(new(42, [1, 2, 3]));
        }
        public Task<ImapOperationResult<IReadOnlyList<ImapMessageHeader>>> FetchHeadersAsync(ImapConnectionSettings c, string f, IReadOnlyList<uint> u, uint? v = null, CancellationToken t = default) => throw new NotSupportedException();
        public Task<ImapOperationResult<MimeMessage>> FetchMessageAsync(ImapConnectionSettings c, string f, uint u, uint? v = null, CancellationToken t = default) => throw new NotSupportedException();
        public Task<ImapOperationResult<IReadOnlyList<string>>> ListFoldersAsync(ImapConnectionSettings c, CancellationToken t = default) => throw new NotSupportedException();
        public Task<ImapOperationResult<uint?>> AppendMessageAsync(ImapConnectionSettings c, string f, MimeMessage m, CancellationToken t = default, DateTimeOffset? d = null) => throw new NotSupportedException();
        public Task<ImapOperationResult<int>> DeleteAndExpungeAsync(ImapConnectionSettings c, string f, IReadOnlyList<uint> u, uint v, CancellationToken t = default) => throw new NotSupportedException();
        public Task<ImapOperationResult<bool>> TestConnectionAsync(ImapConnectionSettings c, CancellationToken t = default) => throw new NotSupportedException();
    }
}
