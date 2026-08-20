namespace MailWinnow.Web.Components.Layout;

public static class NavigationCountLoader
{
    public static Task RunAsync(
        Func<CancellationToken, Task> loadInboxCountAsync,
        Func<CancellationToken, Task> loadReviewCountsAsync,
        TimeSpan refreshInterval,
        CancellationToken cancellationToken) =>
        RunAsync(loadInboxCountAsync, loadReviewCountsAsync,
            token => Task.Delay(refreshInterval, token), cancellationToken);

    internal static async Task RunAsync(
        Func<CancellationToken, Task> loadInboxCountAsync,
        Func<CancellationToken, Task> loadReviewCountsAsync,
        Func<CancellationToken, Task> waitForNextRefreshAsync,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.WhenAll(
                    IgnoreFailureAsync(loadInboxCountAsync, cancellationToken),
                    IgnoreFailureAsync(loadReviewCountsAsync, cancellationToken));
                await waitForNextRefreshAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task IgnoreFailureAsync(Func<CancellationToken, Task> loader, CancellationToken cancellationToken)
    {
        try
        {
            await loader(cancellationToken);
        }
        catch
        {
            // Each component loader logs its own failure. Keep the loop alive so a later refresh can recover.
        }
    }
}
