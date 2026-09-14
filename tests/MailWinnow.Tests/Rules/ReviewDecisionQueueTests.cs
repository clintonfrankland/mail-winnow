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
        // Evaluation has returned, but stopping at that point used to race the following SaveChanges.
        // Wait for the durable completion boundary before simulating the second shutdown.
        await EventuallyAsync(async () =>
        {
            await using var check = provider.CreateAsyncScope();
            return (await check.ServiceProvider.GetRequiredService<MailWinnowDbContext>()
                .ReviewDecisionWorkItems.FindAsync(id))?.Status == ReviewDecisionWorkStatus.Completed;
        });
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

    [Fact]
    public async Task LaterRepeatAfterOppositeDecisionIsAcceptedAndAppliesFinalAction()
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
        await using (var setup = provider.CreateAsyncScope())
            await setup.ServiceProvider.GetRequiredService<MailWinnowDbContext>().Database.EnsureCreatedAsync();
        var queue = new ReviewDecisionQueue(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<ReviewDecisionQueue>.Instance);
        await queue.StartAsync(CancellationToken.None);

        foreach (var action in new[] { RuleAction.PermanentlyAllow, RuleAction.PermanentlyBlock, RuleAction.PermanentlyAllow })
        {
            Assert.True((await queue.QueueAsync(Principal("owner"), action, RuleMatchType.ExactSender, "repeat@test", null, [])).Succeeded);
            await EventuallyAsync(async () =>
            {
                await using var scope = provider.CreateAsyncScope();
                return (await scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>().ReviewDecisionWorkItems
                    .ToListAsync()).OrderByDescending(x => x.CreatedUtc).First().Status == ReviewDecisionWorkStatus.Completed;
            });
        }
        await queue.StopAsync(CancellationToken.None);

        Assert.Equal(new[] { RuleAction.PermanentlyAllow, RuleAction.PermanentlyBlock, RuleAction.PermanentlyAllow }, rules.Actions);
        await using var verification = provider.CreateAsyncScope();
        Assert.Equal(3, await verification.ServiceProvider.GetRequiredService<MailWinnowDbContext>().ReviewDecisionWorkItems.CountAsync());
    }

    [Fact]
    public async Task TransientFailureIsRetriedAndCompletedDurably()
    {
        var rules = new RecordingRuleManagementService();
        var evaluations = new FailingEvaluationService(failuresBeforeSuccess: 1);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContext<MailWinnowDbContext>(options => options.UseSqlite(connection));
        services.AddScoped<IOwnershipAuthorizer, OwnershipAuthorizer>();
        services.AddScoped<IRuleManagementService>(_ => rules);
        services.AddScoped<IRuleEvaluationService>(_ => evaluations);
        await using var provider = services.BuildServiceProvider();
        await using (var setup = provider.CreateAsyncScope())
            await setup.ServiceProvider.GetRequiredService<MailWinnowDbContext>().Database.EnsureCreatedAsync();
        var queue = new ReviewDecisionQueue(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<ReviewDecisionQueue>.Instance);
        Assert.True((await queue.QueueAsync(Principal("owner"), RuleAction.PermanentlyAllow,
            RuleMatchType.ExactSender, "retry@test", null, [])).Succeeded);
        await queue.StartAsync(CancellationToken.None);

        // Let the worker schedule its own retry.  The assertion never writes through a second
        // context while the worker owns the shared SQLite connection.
        await evaluations.Success.Task.WaitAsync(TimeSpan.FromSeconds(8));
        await Task.Delay(100);
        await queue.StopAsync(CancellationToken.None);

        await using var verification = provider.CreateAsyncScope();
        var completed = await verification.ServiceProvider.GetRequiredService<MailWinnowDbContext>().ReviewDecisionWorkItems.SingleAsync();
        Assert.Equal(2, completed.AttemptCount);
        Assert.Equal(ReviewDecisionWorkStatus.Completed, completed.Status);
    }

    [Fact]
    public async Task RepeatedFailuresBecomeTerminalAndRemainObservable()
    {
        var evaluations = new FailingEvaluationService(failuresBeforeSuccess: int.MaxValue);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContext<MailWinnowDbContext>(options => options.UseSqlite(connection));
        services.AddScoped<IOwnershipAuthorizer, OwnershipAuthorizer>();
        services.AddScoped<IRuleManagementService, RecordingRuleManagementService>();
        services.AddScoped<IRuleEvaluationService>(_ => evaluations);
        await using var provider = services.BuildServiceProvider();
        await using (var setup = provider.CreateAsyncScope())
            await setup.ServiceProvider.GetRequiredService<MailWinnowDbContext>().Database.EnsureCreatedAsync();
        var queue = new ReviewDecisionQueue(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<ReviewDecisionQueue>.Instance);
        Assert.True((await queue.QueueAsync(Principal("owner"), RuleAction.PermanentlyBlock,
            RuleMatchType.ExactSender, "fail@test", null, [])).Succeeded);
        await queue.StartAsync(CancellationToken.None);

        for (var attempt = 1; attempt < 3; attempt++)
        {
            await evaluations.WaitForFailuresAsync(attempt);
            await EventuallyAsync(async () =>
            {
                await using var scope = provider.CreateAsyncScope();
                var item = await scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>().ReviewDecisionWorkItems.SingleAsync();
                if (item.Status != ReviewDecisionWorkStatus.Retrying) return false;
                item.NextAttemptUtc = DateTimeOffset.UtcNow;
                await scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>().SaveChangesAsync();
                return true;
            });
        }
        await EventuallyAsync(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            return (await scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>().ReviewDecisionWorkItems.SingleAsync()).Status == ReviewDecisionWorkStatus.Failed;
        });
        await queue.StopAsync(CancellationToken.None);

        var metrics = await queue.GetMetricsAsync();
        Assert.Equal(1, metrics.TerminalFailures);
    }

    [Fact]
    public async Task AcceptedClickIsDurableWithoutStartingBackgroundProcessing()
    {
        var evaluation = new BlockingEvaluationService();
        await using var fixture = await QueueFixture.CreateAsync(evaluation);
        var accepted = await fixture.Queue.QueueAsync(Principal("owner"), RuleAction.PermanentlyBlock,
            RuleMatchType.ExactSender, "quick@test", null, []);
        Assert.True(accepted.Succeeded);
        Assert.False(evaluation.Entered.Task.IsCompleted);
        await using var scope = fixture.Provider.CreateAsyncScope();
        Assert.Equal(ReviewDecisionWorkStatus.Pending, (await scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>()
            .ReviewDecisionWorkItems.SingleAsync()).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SupersededAttemptCannotCompleteOrRescheduleNewOwner(bool failEvaluation)
    {
        var evaluation = new BlockingEvaluationService { Fail = failEvaluation };
        await using var fixture = await QueueFixture.CreateAsync(evaluation);
        await fixture.Queue.QueueAsync(Principal("owner"), RuleAction.PermanentlyAllow,
            RuleMatchType.ExactSender, "owned@test", null, []);
        var processing = ProcessBatchAsync(fixture.Queue);
        await evaluation.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using (var scope = fixture.Provider.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
            var item = await database.ReviewDecisionWorkItems.SingleAsync();
            // Simulate a lease expiry/reclaim while an uncooperative old evaluation is finishing.
            item.AttemptCount++;
            item.NextAttemptUtc = DateTimeOffset.UtcNow.AddMinutes(2);
            await database.SaveChangesAsync();
        }
        evaluation.Release.TrySetResult();
        await processing.WaitAsync(TimeSpan.FromSeconds(5));
        await using var verification = fixture.Provider.CreateAsyncScope();
        var retained = await verification.ServiceProvider.GetRequiredService<MailWinnowDbContext>().ReviewDecisionWorkItems.SingleAsync();
        Assert.Equal(ReviewDecisionWorkStatus.Processing, retained.Status);
        Assert.Equal(2, retained.AttemptCount);
        Assert.Null(retained.CompletedUtc);
        Assert.Null(retained.LastError);
    }

    [Fact]
    public async Task LongRunningAttemptRenewsLeaseAndCannotBeReclaimedByAnotherProcessor()
    {
        var evaluation = new BlockingEvaluationService();
        await using var fixture = await QueueFixture.CreateAsync(evaluation);
        await fixture.Queue.QueueAsync(Principal("owner"), RuleAction.PermanentlyAllow,
            RuleMatchType.ExactSender, "long@test", null, []);
        var processing = ProcessBatchAsync(fixture.Queue);
        await evaluation.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        DateTimeOffset initialExpiry;
        await using (var scope = fixture.Provider.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
            var item = await database.ReviewDecisionWorkItems.SingleAsync();
            initialExpiry = item.NextAttemptUtc;
            item.StartedUtc = DateTimeOffset.UtcNow.AddMinutes(-5);
            await database.SaveChangesAsync();
        }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (true)
            {
                await Task.Delay(100, timeout.Token);
                await using var check = fixture.Provider.CreateAsyncScope();
                var item = await check.ServiceProvider.GetRequiredService<MailWinnowDbContext>().ReviewDecisionWorkItems.SingleAsync(timeout.Token);
                if (item.NextAttemptUtc > initialExpiry) break;
            }
            var secondProcessor = new ReviewDecisionQueue(fixture.Provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<ReviewDecisionQueue>.Instance);
            Assert.False(await ProcessBatchAsync(secondProcessor));
            Assert.Equal(1, evaluation.Calls);
        }
        finally
        {
            evaluation.Release.TrySetResult();
            await processing.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task SelectionFiltersFutureRetriesBeforeTakingBoundedBatch()
    {
        var evaluations = new RecordingEvaluationService();
        await using var fixture = await QueueFixture.CreateAsync(evaluations);
        await using (var scope = fixture.Provider.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
            for (var index = 0; index < 60; index++)
                database.ReviewDecisionWorkItems.Add(new ReviewDecisionWorkItem
                {
                    OwnerUserId = "owner", Action = RuleAction.PermanentlyBlock, MatchType = RuleMatchType.ExactSender,
                    MatchValue = $"sender{index}@test", IdempotencyKey = $"test-{index}", MessageIdsJson = "[]",
                    Status = ReviewDecisionWorkStatus.Retrying,
                    CreatedUtc = DateTimeOffset.UtcNow.AddMinutes(-60 + index),
                    NextAttemptUtc = index < 40 ? DateTimeOffset.UtcNow.AddDays(1) : DateTimeOffset.UtcNow.AddMinutes(-1)
                });
            await database.SaveChangesAsync();
        }
        Assert.True(await ProcessBatchAsync(fixture.Queue));
        Assert.Equal(16, evaluations.Owners.Count);
        await using var verification = fixture.Provider.CreateAsyncScope();
        var databaseCheck = verification.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
        Assert.Equal(16, await databaseCheck.ReviewDecisionWorkItems.CountAsync(x => x.Status == ReviewDecisionWorkStatus.Completed));
        Assert.Equal(44, await databaseCheck.ReviewDecisionWorkItems.CountAsync(x => x.Status == ReviewDecisionWorkStatus.Retrying));
    }

    private static Task<bool> ProcessBatchAsync(ReviewDecisionQueue queue) => (Task<bool>)typeof(ReviewDecisionQueue)
        .GetMethod("ProcessBatchAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .Invoke(queue, [CancellationToken.None])!;

    private sealed class BlockingEvaluationService : IRuleEvaluationService
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Fail { get; init; }
        public int Calls { get; private set; }
        public Task<RuleEvaluation> EvaluateAsync(string ownerUserId, Guid headerId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReevaluatePendingHeadersAsync(string ownerUserId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async Task ReevaluateOwnedHeadersAsync(string ownerUserId, CancellationToken cancellationToken = default)
        {
            Calls++;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            if (Fail) throw new InvalidOperationException("Expected failure from superseded attempt.");
        }
    }

    private sealed class QueueFixture(SqliteConnection connection, ServiceProvider provider) : IAsyncDisposable
    {
        public ServiceProvider Provider => provider;
        public ReviewDecisionQueue Queue { get; } = new(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<ReviewDecisionQueue>.Instance);
        public static async Task<QueueFixture> CreateAsync(IRuleEvaluationService evaluation)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var services = new ServiceCollection();
            services.AddDbContext<MailWinnowDbContext>(options => options.UseSqlite(connection));
            services.AddScoped<IOwnershipAuthorizer, OwnershipAuthorizer>();
            services.AddScoped<IRuleManagementService, RecordingRuleManagementService>();
            services.AddScoped<IRuleEvaluationService>(_ => evaluation);
            var provider = services.BuildServiceProvider();
            await using var scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>().Database.EnsureCreatedAsync();
            return new QueueFixture(connection, provider);
        }
        public async ValueTask DisposeAsync()
        {
            Queue.Dispose();
            await provider.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!timeout.IsCancellationRequested)
        {
            if (await condition()) return;
            await Task.Delay(25, timeout.Token);
        }
        Assert.Fail("Condition did not become true before timeout.");
    }

    private static ClaimsPrincipal Principal(string id) => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "test"));

    private sealed class RecordingRuleManagementService : IRuleManagementService
    {
        private int _active;
        public List<string> Values { get; } = [];
        public List<RuleAction> Actions { get; } = [];
        public int MaximumConcurrency { get; private set; }
        public TaskCompletionSource AllApplied { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task AddOrUpdateAsync(MailRule rule, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Queue must persist then evaluate once.");

        public async Task PersistAsync(MailRule rule, CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref _active);
            MaximumConcurrency = Math.Max(MaximumConcurrency, active);
            await Task.Delay(20, cancellationToken);
            Values.Add(rule.MatchValue);
            Actions.Add(rule.Action);
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

    private sealed class FailingEvaluationService(int failuresBeforeSuccess) : IRuleEvaluationService
    {
        private int _failuresRemaining = failuresBeforeSuccess;
        private int _failureCount;
        public TaskCompletionSource FirstFailure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Success { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<RuleEvaluation> EvaluateAsync(string ownerUserId, Guid headerId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReevaluatePendingHeadersAsync(string ownerUserId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReevaluateOwnedHeadersAsync(string ownerUserId, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Decrement(ref _failuresRemaining) >= 0)
            {
                var count = Interlocked.Increment(ref _failureCount);
                FirstFailure.TrySetResult();
                throw new InvalidOperationException($"Expected test failure {count}.");
            }
            Success.TrySetResult();
            return Task.CompletedTask;
        }

        public async Task WaitForFailuresAsync(int expected) => await EventuallyAsync(() => Task.FromResult(Volatile.Read(ref _failureCount) >= expected));
    }
}
