using System.Security.Claims;
using MailWinnow.Infrastructure.Mailboxes;
using MailWinnow.Infrastructure.Persistence;
using MailWinnow.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MailWinnow.Tests.Mailboxes;

public sealed class InboxRefreshTests
{
    private static ClaimsPrincipal Actor(string owner = "owner") => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, owner)], "test"));

    [Fact]
    public async Task Requests_are_durable_coalesced_owner_scoped_and_never_need_an_imap_service()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using (var db = fixture.CreateContext())
        {
            db.SourceMailboxes.AddRange(Source(), Source("other"), Source(enabled: false));
            await db.SaveChangesAsync();
        }
        var first = await fixture.Service.RequestAsync(Actor());
        var recreatedService = new InboxRefreshService(fixture.Provider.GetRequiredService<IServiceScopeFactory>());
        var second = await recreatedService.RequestAsync(Actor());
        Assert.True(first.Succeeded);
        Assert.Equal(1, first.MailboxCount);
        Assert.Equal(1, second.MailboxCount);
        await using var verification = fixture.CreateContext();
        var rows = await verification.SourceMailboxes.AsNoTracking().ToArrayAsync();
        Assert.Equal(3, rows.Length);
        Assert.Equal(second.RequestedUtc, Assert.Single(rows, source => source.Enabled && source.OwnerUserId == "owner").SyncRequestedUtc);
        Assert.All(rows.Where(source => !source.Enabled || source.OwnerUserId != "owner"), source => Assert.Null(source.SyncRequestedUtc));
        var afterRestart = await recreatedService.GetStatusAsync(Actor(), second.RequestedUtc);
        Assert.Equal(1, afterRestart.WaitingSources);
        Assert.False(afterRestart.IsComplete);
        Assert.True(await new MailSyncWakeSignal(fixture.Provider.GetRequiredService<IServiceScopeFactory>()).HasPendingRequestAsync());
    }

    [Fact]
    public async Task Claim_covers_a_request_accepted_after_start_timestamp_was_sampled()
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = Source();
        await using (var db = fixture.CreateContext()) { db.Add(source); await db.SaveChangesAsync(); }
        var staleStartedUtc = DateTimeOffset.UtcNow.AddSeconds(-1);
        var request = await fixture.Service.RequestAsync(Actor());
        await using (var db = fixture.CreateContext())
        {
            Assert.Equal(1, await SourceMailboxSynchronizer.ClaimRequestAsync(db, source.Id, staleStartedUtc));
            var claimed = await db.SourceMailboxes.AsNoTracking().SingleAsync();
            Assert.Equal(request.RequestedUtc, claimed.LastSyncAttemptUtc);
            Assert.Null(claimed.SyncRequestedUtc);
        }
        var result = await fixture.Service.GetStatusAsync(Actor(), request.RequestedUtc);
        Assert.Equal(1, result.SyncingSources);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public async Task Concurrent_requests_committing_in_reverse_order_preserve_the_newer_marker()
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = Source();
        var earlier = DateTimeOffset.UtcNow.AddSeconds(-1);
        var later = DateTimeOffset.UtcNow;
        await using (var db = fixture.CreateContext())
        {
            db.Add(source); await db.SaveChangesAsync();
            Assert.Equal(1, await InboxRefreshService.UpdateRequestMarkersAsync(db, "owner", later));
            Assert.Equal(1, await InboxRefreshService.UpdateRequestMarkersAsync(db, "owner", earlier));
            Assert.Equal(later, (await db.SourceMailboxes.AsNoTracking().SingleAsync()).SyncRequestedUtc);
        }
        var status = await fixture.Service.GetStatusAsync(Actor(), later);
        Assert.Equal(1, status.WaitingSources);
        Assert.False(status.IsComplete);
    }

    [Fact]
    public async Task Unauthenticated_actor_cannot_request_or_observe_refreshes()
    {
        await using var fixture = await Fixture.CreateAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.RequestAsync(new ClaimsPrincipal()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.GetStatusAsync(new ClaimsPrincipal(), DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task No_enabled_sources_returns_a_clear_noop_and_disabled_markers_do_not_wake_worker()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using (var db = fixture.CreateContext())
        {
            var disabled = Source(enabled: false);
            disabled.SyncRequestedUtc = DateTimeOffset.UtcNow;
            db.SourceMailboxes.Add(disabled);
            await db.SaveChangesAsync();
        }
        var request = await fixture.Service.RequestAsync(Actor());
        Assert.Equal(0, request.MailboxCount);
        Assert.Contains("No enabled source", request.Message);
        Assert.True((await fixture.Service.GetStatusAsync(Actor(), request.RequestedUtc)).IsComplete);
        Assert.False(await new MailSyncWakeSignal(fixture.Provider.GetRequiredService<IServiceScopeFactory>()).HasPendingRequestAsync());
    }

    [Fact]
    public async Task Progress_reports_only_current_owned_work_and_latest_inbox_change_without_loading_messages()
    {
        await using var fixture = await Fixture.CreateAsync();
        var requestedUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        var deliveredUtc = requestedUtc.AddSeconds(20);
        var deletedUtc = requestedUtc.AddSeconds(30);
        await using (var db = fixture.CreateContext())
        {
            var synchronized = Source(); synchronized.LastSyncAttemptUtc = requestedUtc.AddSeconds(1); synchronized.PollingStatus = "Synchronized";
            var synchronizing = Source(); synchronizing.LastSyncAttemptUtc = requestedUtc.AddSeconds(2); synchronizing.PollingStatus = "Synchronizing";
            var failed = Source(); failed.LastSyncAttemptUtc = requestedUtc.AddSeconds(3); failed.PollingStatus = "Failed";
            var oldFailure = Source(); oldFailure.LastSyncAttemptUtc = requestedUtc.AddDays(-1); oldFailure.PollingStatus = "Failed";
            var other = Source("other"); other.LastSyncAttemptUtc = requestedUtc.AddSeconds(4); other.PollingStatus = "Failed";
            var disabled = Source(enabled: false); disabled.SyncRequestedUtc = requestedUtc.AddSeconds(4);
            db.SourceMailboxes.AddRange(synchronized, synchronizing, failed, oldFailure, other, disabled);
            AddDelivery(db, synchronized, MessageDeliveryState.Pending, requestedUtc.AddDays(-1));
            AddDelivery(db, synchronized, MessageDeliveryState.RetryPending, requestedUtc.AddDays(-1));
            AddDelivery(db, synchronized, MessageDeliveryState.Failed, requestedUtc.AddSeconds(1));
            AddDelivery(db, synchronized, MessageDeliveryState.Failed, requestedUtc.AddDays(-1));
            var delivered = AddDelivery(db, synchronized, MessageDeliveryState.Deleted, requestedUtc.AddDays(-1));
            delivered.DeliveredUtc = deliveredUtc; delivered.DeletedUtc = deletedUtc;
            var deliveredOther = AddDelivery(db, other, MessageDeliveryState.Delivered, requestedUtc.AddSeconds(1)); deliveredOther.DeliveredUtc = deletedUtc.AddDays(1);
            AddDelivery(db, disabled, MessageDeliveryState.Pending, requestedUtc);
            AddDelivery(db, other, MessageDeliveryState.Failed, requestedUtc.AddSeconds(5));
            await db.SaveChangesAsync();
        }
        var result = await fixture.Service.GetStatusAsync(Actor(), requestedUtc);
        Assert.Equal(0, result.WaitingSources);
        Assert.Equal(1, result.SyncingSources);
        Assert.Equal(1, result.FailedSources);
        Assert.Equal(2, result.PendingDeliveries);
        Assert.Equal(1, result.FailedDeliveries);
        Assert.Equal(deletedUtc, result.LatestInboxChangeUtc);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public async Task Disabled_removed_or_new_unrequested_sources_do_not_leave_refresh_waiting_forever()
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = Source();
        await using (var db = fixture.CreateContext()) { db.Add(source); await db.SaveChangesAsync(); }
        var requested = await fixture.Service.RequestAsync(Actor());
        await using (var db = fixture.CreateContext())
        {
            await db.SourceMailboxes.Where(row => row.Id == source.Id).ExecuteUpdateAsync(update => update.SetProperty(row => row.Enabled, false));
            db.Add(Source()); await db.SaveChangesAsync();
        }
        var result = await fixture.Service.GetStatusAsync(Actor(), requested.RequestedUtc);
        Assert.True(result.IsComplete);
        Assert.Equal(0, result.WaitingSources);
    }

    [Fact]
    public async Task Wake_checks_every_two_seconds_but_does_not_start_cycles_without_a_request()
    {
        var checks = 0;
        var delays = new List<TimeSpan>();
        await MailSyncWakeSignal.WaitForRequestAsync(TimeSpan.FromSeconds(7), _ => { checks++; return Task.FromResult(false); },
            (delay, _) => { delays.Add(delay); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1)], delays);
        Assert.Equal(4, checks);
        checks = 0; delays.Clear();
        await MailSyncWakeSignal.WaitForRequestAsync(TimeSpan.FromSeconds(300), _ => Task.FromResult(++checks == 2),
            (delay, _) => { delays.Add(delay); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(2, delays.Count);
    }

    [Fact]
    public async Task Wake_query_failure_falls_back_to_normal_schedule_without_a_retry_storm()
    {
        var checks = 0;
        var failures = 0;
        var delays = new List<TimeSpan>();
        await MailSyncWakeSignal.WaitForRequestAsync(TimeSpan.FromSeconds(300), _ =>
            { checks++; throw new InvalidOperationException("database unavailable"); },
            (delay, _) => { delays.Add(delay); return Task.CompletedTask; }, CancellationToken.None, () => failures++);
        Assert.Equal(1, checks);
        Assert.Equal(1, failures);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(298)], delays);
    }

    [Fact]
    public async Task Wake_remains_cancellable_without_touching_mailboxes()
    {
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MailSyncWakeSignal.WaitForRequestAsync(TimeSpan.FromSeconds(300),
            _ => Task.FromResult(false), (delay, token) => { cancellation.Cancel(); return Task.Delay(delay, token); }, cancellation.Token));
    }

    private static SourceMailbox Source(string owner = "owner", bool enabled = true) => new()
    {
        OwnerUserId = owner, DisplayName = "source", Host = "unused.invalid", Username = "unused", ProtectedCredential = "unused", Enabled = enabled
    };

    private static MessageDelivery AddDelivery(MailWinnowDbContext db, SourceMailbox source, MessageDeliveryState state, DateTimeOffset createdUtc)
    {
        var header = new SourceMessageHeader { SourceMailboxId = source.Id, FolderName = "INBOX", UidValidity = 1, Uid = (uint)Random.Shared.Next(1, int.MaxValue) };
        var delivery = new MessageDelivery { SourceMessageHeaderId = header.Id, OwnerUserId = source.OwnerUserId, State = state, CreatedUtc = createdUtc };
        db.Add(header); db.Add(delivery); return delivery;
    }

    private sealed class Fixture(SqliteConnection connection, ServiceProvider provider) : IAsyncDisposable
    {
        public ServiceProvider Provider => provider;
        public InboxRefreshService Service => new(provider.GetRequiredService<IServiceScopeFactory>());
        public MailWinnowDbContext CreateContext() => new(new DbContextOptionsBuilder<MailWinnowDbContext>().UseSqlite(connection).Options);
        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
            var services = new ServiceCollection().AddDbContext<MailWinnowDbContext>(options => options.UseSqlite(connection))
                .AddScoped<IOwnershipAuthorizer, OwnershipAuthorizer>();
            var fixture = new Fixture(connection, services.BuildServiceProvider());
            await using var db = fixture.CreateContext(); await db.Database.EnsureCreatedAsync();
            return fixture;
        }
        public async ValueTask DisposeAsync() { await provider.DisposeAsync(); await connection.DisposeAsync(); }
    }
}
