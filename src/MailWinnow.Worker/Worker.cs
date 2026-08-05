namespace MailWinnow.Worker;

using MailWinnow.Infrastructure.Mailboxes;
using Microsoft.Extensions.Options;

public class Worker(ILogger<Worker> logger, IServiceScopeFactory scopes, IMailSyncQueue queue, IMessageDeliveryService deliveries, IOptions<MailSyncOptions> options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var ids = await queue.GetDueMailboxIdsAsync(stoppingToken);
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
                var deliveryIds = await deliveries.GetDueDeliveryIdsAsync(stoppingToken);
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
