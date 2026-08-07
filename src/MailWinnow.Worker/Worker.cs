namespace MailWinnow.Worker;

using MailWinnow.Infrastructure.Mailboxes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

public class Worker(ILogger<Worker> logger, IServiceScopeFactory scopes, IMailSyncQueue queue, IOptions<MailSyncOptions> options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var ids = await queue.GetDueMailboxIdsAsync(stoppingToken);
                await using (var heartbeatScope = scopes.CreateAsyncScope())
                {
                    var heartbeatDb = heartbeatScope.ServiceProvider.GetRequiredService<MailWinnow.Infrastructure.Persistence.MailWinnowDbContext>();
                    var heartbeat = await heartbeatDb.WorkerHeartbeats
                        .OrderBy(entry => entry.Id)
                        .FirstOrDefaultAsync(stoppingToken)
                        ?? new MailWinnow.Infrastructure.Security.WorkerHeartbeat();
                    if (heartbeatDb.Entry(heartbeat).State == Microsoft.EntityFrameworkCore.EntityState.Detached)
                    {
                        heartbeatDb.WorkerHeartbeats.Add(heartbeat);
                    }
                    heartbeat.LastSeenUtc = DateTimeOffset.UtcNow; heartbeat.Status = "Running";
                    await heartbeatDb.SaveChangesAsync(stoppingToken);
                }
                var maximumConcurrency = Math.Clamp(options.Value.MaximumConcurrency, 1, 32);
                await Parallel.ForEachAsync(ids, new ParallelOptions { MaxDegreeOfParallelism = maximumConcurrency, CancellationToken = stoppingToken }, async (id, token) =>
                {
                    try
                    {
                        await using var scope = scopes.CreateAsyncScope();
                        await scope.ServiceProvider.GetRequiredService<ISourceMailboxSynchronizer>().SynchronizeAsync(id, token);
                    }
                    catch (Exception exception) when (!token.IsCancellationRequested)
                    {
                        // Account failures are intentionally isolated; the synchronizer records sanitized status for known accounts.
                        logger.LogWarning(exception, "Header synchronization failed for source mailbox {MailboxId}", id);
                    }
                });
                await using (var blockedScope = scopes.CreateAsyncScope())
                {
                    var blockedIds = await blockedScope.ServiceProvider.GetRequiredService<IBlockedMessageDeletionService>().GetDueHeaderIdsAsync(stoppingToken);
                    await Parallel.ForEachAsync(blockedIds, new ParallelOptions { MaxDegreeOfParallelism = maximumConcurrency, CancellationToken = stoppingToken }, async (id, token) =>
                    {
                        try
                        {
                            await using var scope = scopes.CreateAsyncScope();
                            await scope.ServiceProvider.GetRequiredService<IBlockedMessageDeletionService>().DeleteAsync(id, token);
                        }
                        catch (Exception exception) when (!token.IsCancellationRequested)
                        {
                            logger.LogWarning(exception, "Blocked source deletion failed for header {HeaderId}", id);
                        }
                    });
                }
                await using var deliveryScope = scopes.CreateAsyncScope();
                var deliveryIds = await deliveryScope.ServiceProvider.GetRequiredService<IMessageDeliveryService>().GetDueDeliveryIdsAsync(stoppingToken);
                await Parallel.ForEachAsync(deliveryIds, new ParallelOptions { MaxDegreeOfParallelism = maximumConcurrency, CancellationToken = stoppingToken }, async (id, token) =>
                {
                    try
                    {
                        await using var scope = scopes.CreateAsyncScope();
                        await scope.ServiceProvider.GetRequiredService<IMessageDeliveryService>().DeliverAsync(id, token);
                    }
                    catch (Exception exception) when (!token.IsCancellationRequested)
                    {
                        logger.LogWarning(exception, "Approved message delivery failed for delivery {DeliveryId}", id);
                    }
                });
                var cleanupIds = await deliveryScope.ServiceProvider.GetRequiredService<IMessageDeliveryService>().GetDueCleanupIdsAsync(stoppingToken);
                await Parallel.ForEachAsync(cleanupIds, new ParallelOptions { MaxDegreeOfParallelism = maximumConcurrency, CancellationToken = stoppingToken }, async (id, token) =>
                {
                    try
                    {
                        await using var scope = scopes.CreateAsyncScope();
                        await scope.ServiceProvider.GetRequiredService<IMessageDeliveryService>().CleanupExpiredAsync(id, token);
                    }
                    catch (Exception exception) when (!token.IsCancellationRequested)
                    {
                        logger.LogWarning(exception, "Delivered message cleanup failed for delivery {DeliveryId}", id);
                    }
                });
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(exception, "Mail synchronization worker cycle failed.");
            }
            var interval = TimeSpan.FromSeconds(Math.Clamp(options.Value.PollingIntervalSeconds, 15, 86400));
            await Task.Delay(interval, stoppingToken);
        }
    }
}
