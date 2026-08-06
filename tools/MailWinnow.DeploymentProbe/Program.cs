using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using Microsoft.Data.SqlClient;

const string defaultImapHost = "mailwinnow-imap.clintandtara.com";
var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__MailWinnow");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("ConnectionStrings__MailWinnow is required.");
}

var imapHost = Environment.GetEnvironmentVariable("LocalImap__Host") ?? defaultImapHost;
var imapPort = int.TryParse(Environment.GetEnvironmentVariable("LocalImap__Port"), out var configuredPort)
    ? configuredPort
    : 993;
var useSsl = bool.TryParse(Environment.GetEnvironmentVariable("LocalImap__UseSsl"), out var configuredSsl) && configuredSsl;
var allowInvalidCertificate = bool.TryParse(
    Environment.GetEnvironmentVariable("LocalImap__AllowInvalidCertificate"),
    out var configuredBypass) && configuredBypass;

if (!useSsl || allowInvalidCertificate)
{
    throw new InvalidOperationException("Deployment probe requires TLS with invalid-certificate bypass disabled.");
}

var addresses = await Dns.GetHostAddressesAsync(imapHost);
using var tcp = new TcpClient();
await tcp.ConnectAsync(imapHost, imapPort);
await using var tls = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
{
    TargetHost = imapHost,
    EnabledSslProtocols = SslProtocols.None,
    CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.Online
});

var builder = new SqlConnectionStringBuilder(connectionString);
await using var database = new SqlConnection(connectionString);
await database.OpenAsync();
await using var heartbeatCommand = database.CreateCommand();
heartbeatCommand.CommandText = "SELECT MAX([LastSeenUtc]) FROM [WorkerHeartbeats]";
var heartbeatValue = await heartbeatCommand.ExecuteScalarAsync();
var heartbeatUtc = heartbeatValue is DateTimeOffset timestamp ? timestamp : (DateTimeOffset?)null;

Console.WriteLine(JsonSerializer.Serialize(new
{
    database = builder.InitialCatalog,
    databaseReachable = true,
    workerHeartbeatObserved = heartbeatUtc.HasValue,
    workerHeartbeatAgeSeconds = heartbeatUtc.HasValue
        ? Math.Max(0, (int)(DateTimeOffset.UtcNow - heartbeatUtc.Value).TotalSeconds)
        : (int?)null,
    imapHost,
    imapPort,
    resolvedAddressCount = addresses.Length,
    tcpReachable = true,
    tlsChainValid = true,
    tlsProtocol = tls.SslProtocol.ToString(),
    invalidCertificateBypass = false
}));
