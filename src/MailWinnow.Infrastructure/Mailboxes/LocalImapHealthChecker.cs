using System.Net.Sockets;

namespace MailWinnow.Infrastructure.Mailboxes;

public interface ILocalImapHealthChecker
{
    Task<string> CheckAsync(LocalImapOptions options, CancellationToken cancellationToken = default);
}

/// <summary>Performs a bounded TCP probe without using or exposing a mailbox credential.</summary>
public sealed class LocalImapHealthChecker : ILocalImapHealthChecker
{
    public async Task<string> CheckAsync(LocalImapOptions options, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.Host) || options.Port is < 1 or > 65535) return "Not configured";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var client = new TcpClient();
            await client.ConnectAsync(options.Host, options.Port, timeout.Token);
            return "Reachable";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return "Unreachable"; }
    }
}
