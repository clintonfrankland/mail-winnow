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
}
