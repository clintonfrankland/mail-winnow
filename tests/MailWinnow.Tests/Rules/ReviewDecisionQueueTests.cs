using System.Security.Claims;
using MailWinnow.Core.Rules;
using MailWinnow.Infrastructure.Persistence;
using MailWinnow.Infrastructure.Rules;
using MailWinnow.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MailWinnow.Tests.Rules;

public sealed class ReviewDecisionQueueTests
{
    [Fact]
    public async Task RapidReviewDecisionsAreProcessedSerially()
    {
        var rules = new RecordingRuleManagementService();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContext<MailWinnowDbContext>(options => options.UseSqlite(connection));
        services.AddScoped<IOwnershipAuthorizer, OwnershipAuthorizer>();
        services.AddScoped<IRuleManagementService>(_ => rules);
        services.AddScoped<IRuleEvaluationService, RecordingEvaluationService>();
        await using var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>().Database.EnsureCreatedAsync();
        var queue = new ReviewDecisionQueue(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<ReviewDecisionQueue>.Instance);
        await queue.StartAsync(CancellationToken.None);

        var requests = Enumerable.Range(1, 5).Select(index => queue.QueueAsync(Principal("owner"),
            index % 2 == 0 ? RuleAction.PermanentlyBlock : RuleAction.PermanentlyAllow,
            RuleMatchType.ExactSender, $"sender{index}@test", 1, [Guid.NewGuid()]));

        Assert.All(await Task.WhenAll(requests), result => Assert.True(result.Succeeded));
        await rules.AllApplied.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await queue.StopAsync(CancellationToken.None);

        Assert.Equal(Enumerable.Range(1, 5).Select(index => $"sender{index}@test"), rules.Values);
        Assert.Equal(1, rules.MaximumConcurrency);
    }

    [Fact]
    public async Task RestartedIdenticalRuleCompletesItsReevaluationBeforeWorkIsMarkedCompleted()
    {
        var rules = new RecordingRuleManagementService();
        var evaluations = new RecordingEvaluationService();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContext<MailWinnowDbContext>(options => options.UseSqlite(connection));
        services.AddScoped<IOwnershipAuthorizer, OwnershipAuthorizer>();
        services.AddScoped<IRuleManagementService>(_ => rules);
        services.AddScoped<IRuleEvaluationService>(_ => evaluations);
        await using var provider = services.BuildServiceProvider();
        Guid id;
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.MailRules.Add(new MailRule { OwnerUserId = "owner", Action = RuleAction.PermanentlyAllow,
                Scope = RuleScope.User, MatchType = RuleMatchType.ExactSender, MatchValue = "sender@test" });
            await db.SaveChangesAsync();
            var accepted = new ReviewDecisionQueue(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<ReviewDecisionQueue>.Instance);
            Assert.True((await accepted.QueueAsync(Principal("owner"), RuleAction.PermanentlyAllow,
                RuleMatchType.ExactSender, "sender@test", null, [])).Succeeded);
            var item = await db.ReviewDecisionWorkItems.SingleAsync();
            id = item.Id;
            // Simulate a process dying after rule persistence: the original lease is now stale and
            // the rule writer's replay is deliberately a no-op.
            item.Status = ReviewDecisionWorkStatus.Processing;
            item.StartedUtc = DateTimeOffset.UtcNow.AddMinutes(-3);
            await db.SaveChangesAsync();
        }

        var restarted = new ReviewDecisionQueue(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<ReviewDecisionQueue>.Instance);
        await restarted.StartAsync(CancellationToken.None);
        await evaluations.Reevaluated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await restarted.StopAsync(CancellationToken.None);

        await using var verification = provider.CreateAsyncScope();
        Assert.Equal(ReviewDecisionWorkStatus.Completed, (await verification.ServiceProvider.GetRequiredService<MailWinnowDbContext>().ReviewDecisionWorkItems.FindAsync(id))!.Status);
        Assert.Equal(["owner"], evaluations.Owners);
    }

    [Fact]
    public async Task ConcurrentDuplicateClicksCreateOneDurableWorkItem()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContext<MailWinnowDbContext>(options => options.UseSqlite(connection));
        services.AddScoped<IOwnershipAuthorizer, OwnershipAuthorizer>();
        services.AddScoped<IRuleManagementService, RecordingRuleManagementService>();
        services.AddScoped<IRuleEvaluationService, RecordingEvaluationService>();
        await using var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>().Database.EnsureCreatedAsync();
        var queue = new ReviewDecisionQueue(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<ReviewDecisionQueue>.Instance);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => queue.QueueAsync(Principal("owner"),
            RuleAction.PermanentlyBlock, RuleMatchType.ExactSender, "duplicate@test", null, [])));

        Assert.All(outcomes, result => Assert.True(result.Succeeded));
        await using var verification = provider.CreateAsyncScope();
        Assert.Single(await verification.ServiceProvider.GetRequiredService<MailWinnowDbContext>().ReviewDecisionWorkItems.ToListAsync());
    }

    private static ClaimsPrincipal Principal(string id) => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "test"));

    private sealed class RecordingRuleManagementService : IRuleManagementService
    {
        private int _active;
        public List<string> Values { get; } = [];
        public int MaximumConcurrency { get; private set; }
        public TaskCompletionSource AllApplied { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task AddOrUpdateAsync(MailRule rule, CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref _active);
            MaximumConcurrency = Math.Max(MaximumConcurrency, active);
            await Task.Delay(20, cancellationToken);
            Values.Add(rule.MatchValue);
            Interlocked.Decrement(ref _active);
            if (Values.Count == 5) AllApplied.TrySetResult();
        }

        public Task ReplaceAsync(string ownerUserId, Guid ruleId, MailRule replacement, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string ownerUserId, Guid ruleId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetMessageDecisionAsync(MessageDecision decision, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteMessageDecisionAsync(string ownerUserId, Guid headerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RecordingEvaluationService : IRuleEvaluationService
    {
        public List<string> Owners { get; } = [];
        public TaskCompletionSource Reevaluated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<RuleEvaluation> EvaluateAsync(string ownerUserId, Guid headerId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReevaluatePendingHeadersAsync(string ownerUserId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReevaluateOwnedHeadersAsync(string ownerUserId, CancellationToken cancellationToken = default)
        {
            Owners.Add(ownerUserId);
            Reevaluated.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
