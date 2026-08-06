using System.Net;
using System.Net.Sockets;
using MailWinnow.Infrastructure.Mailboxes;

namespace MailWinnow.Tests.Mailboxes;

public sealed class LocalImapHealthCheckerTests
{
    [Fact]
    public async Task Configured_endpoint_is_probed_and_reported_reachable()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var result = await new LocalImapHealthChecker().CheckAsync(new LocalImapOptions { Host = "127.0.0.1", Port = port });

        Assert.Equal("Reachable", result);
    }

    [Fact]
    public async Task Missing_endpoint_is_reported_without_attempting_a_connection()
    {
        var result = await new LocalImapHealthChecker().CheckAsync(new LocalImapOptions());

        Assert.Equal("Not configured", result);
    }
}
