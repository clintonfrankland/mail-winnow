using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MailKit.Net.Smtp;
using MailWinnow.Infrastructure.Outgoing;
using MimeKit;

namespace MailWinnow.Tests.Mailboxes;

/// <summary>Real MailKit protocol tests against an isolated TLS SMTP listener. No external mail.</summary>
public sealed class OutgoingTransportTests
{
    [Fact]
    public async Task Accepted_message_has_one_data_submission_and_does_not_disclose_bcc()
    {
        await using var server = new LocalSmtpServer(ServerBehavior.Accept);
        using var message = CreateMessage();
        message.Bcc.Add(MailboxAddress.Parse("hidden@example.test"));
        var result = await Transport(server).SubmitAsync(Connection(server), message, CancellationToken.None);

        Assert.Equal(SubmissionOutcome.Accepted, result.Outcome);
        Assert.Equal(1, server.DataSubmissions);
        Assert.Contains(server.Commands, command => command.Contains("hidden@example.test", StringComparison.Ordinal));
        Assert.DoesNotContain("Bcc:", server.MessageText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Subject: Test reply", server.MessageText);
    }

    [Fact]
    public async Task Rejected_second_recipient_aborts_before_data_without_partially_sending()
    {
        await using var server = new LocalSmtpServer(ServerBehavior.RejectSecondRecipient);
        using var message = CreateMessage();
        message.To.Add(MailboxAddress.Parse("reject@example.test"));
        var result = await Transport(server).SubmitAsync(Connection(server), message, CancellationToken.None);

        Assert.Equal(SubmissionOutcome.Failed, result.Outcome);
        Assert.Equal(0, server.DataSubmissions);
    }

    [Fact]
    public async Task Lost_final_acknowledgement_is_unknown_not_a_safe_retry()
    {
        await using var server = new LocalSmtpServer(ServerBehavior.DropAfterData);
        using var message = CreateMessage();
        var result = await Transport(server).SubmitAsync(Connection(server), message, CancellationToken.None);

        Assert.Equal(SubmissionOutcome.OutcomeUnknown, result.Outcome);
        Assert.False(result.Retryable);
        Assert.Equal(1, server.DataSubmissions);
    }

    [Fact]
    public async Task Explicit_data_rejection_is_failed_not_sent()
    {
        await using var server = new LocalSmtpServer(ServerBehavior.RejectData);
        using var message = CreateMessage();
        var result = await Transport(server).SubmitAsync(Connection(server), message, CancellationToken.None);
        Assert.Equal(SubmissionOutcome.Failed, result.Outcome);
        Assert.Equal(1, server.DataSubmissions);
    }

    [Fact]
    public async Task Default_transport_rejects_untrusted_server_certificate_before_mail()
    {
        await using var server = new LocalSmtpServer(ServerBehavior.Accept);
        using var message = CreateMessage();
        var result = await new SmtpOutgoingTransport().SubmitAsync(Connection(server), message, CancellationToken.None);
        Assert.Equal(SubmissionOutcome.Failed, result.Outcome);
        Assert.DoesNotContain(server.Commands, command => command.StartsWith("MAIL", StringComparison.Ordinal));
        Assert.Equal(0, server.DataSubmissions);
    }

    [Fact]
    public async Task Required_starttls_upgrades_before_mail_submission()
    {
        await using var server = new LocalSmtpServer(ServerBehavior.Accept, startTls: true);
        using var message = CreateMessage();
        var connection = Connection(server) with { UseStartTls = true };
        var result = await Transport(server).SubmitAsync(connection, message, CancellationToken.None);
        Assert.Equal(SubmissionOutcome.Accepted, result.Outcome);
        Assert.Equal(1, server.DataSubmissions);
        Assert.Contains("STARTTLS", server.Commands);
    }

    private static SendingConnection Connection(LocalSmtpServer server) => new("localhost", server.Port, false, false, "", "");
    private static SmtpOutgoingTransport Transport(LocalSmtpServer server) => new(() => new SmtpClient
    {
        // Test-only trust of this listener's exact ephemeral certificate, never a production bypass.
        ServerCertificateValidationCallback = (_, certificate, _, _) => certificate?.GetCertHashString() == server.Certificate.GetCertHashString()
    });
    private static MimeMessage CreateMessage()
    {
        var message = new MimeMessage { Subject = "Test reply", MessageId = "transport-test@example.test", Body = new TextPart("plain") { Text = "Hello\r\n> quoted text" } };
        message.From.Add(MailboxAddress.Parse("sender@example.test"));
        message.To.Add(MailboxAddress.Parse("recipient@example.test"));
        return message;
    }

    private enum ServerBehavior { Accept, RejectSecondRecipient, DropAfterData, RejectData }
    private sealed class LocalSmtpServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(15));
        private readonly Task run;
        public X509Certificate2 Certificate { get; }
        public int Port { get; }
        public List<string> Commands { get; } = [];
        public string MessageText { get; private set; } = "";
        public int DataSubmissions { get; private set; }

        public LocalSmtpServer(ServerBehavior behavior, bool startTls = false)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var alternateNames = new SubjectAlternativeNameBuilder();
            alternateNames.AddDnsName("localhost");
            request.CertificateExtensions.Add(alternateNames.Build());
            Certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            run = RunAsync(behavior, startTls);
        }

        private async Task RunAsync(ServerBehavior behavior, bool startTls)
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync(lifetime.Token);
                var network = client.GetStream();
                if (startTls)
                {
                    using var greetingReader = new StreamReader(network, leaveOpen: true);
                    await using var greetingWriter = new StreamWriter(network, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
                    await greetingWriter.WriteLineAsync("220 localhost test SMTP");
                    var greeting = await greetingReader.ReadLineAsync(lifetime.Token);
                    if (greeting is null || !greeting.StartsWith("EHLO", StringComparison.Ordinal)) return;
                    Commands.Add(greeting);
                    await greetingWriter.WriteLineAsync("250-localhost\r\n250 STARTTLS");
                    var upgrade = await greetingReader.ReadLineAsync(lifetime.Token);
                    if (upgrade != "STARTTLS") return;
                    Commands.Add(upgrade);
                    await greetingWriter.WriteLineAsync("220 ready for TLS");
                }
                await using var stream = new SslStream(network);
                await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = Certificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                }, lifetime.Token);
                using var reader = new StreamReader(stream);
                await using var writer = new StreamWriter(stream) { NewLine = "\r\n", AutoFlush = true };
                if (!startTls) await writer.WriteLineAsync("220 localhost test SMTP");
                var recipients = 0;
                while (await reader.ReadLineAsync(lifetime.Token) is { } command)
                {
                    Commands.Add(command);
                    if (command.StartsWith("EHLO", StringComparison.Ordinal) || command.StartsWith("HELO", StringComparison.Ordinal))
                        await writer.WriteLineAsync("250 localhost");
                    else if (command.StartsWith("MAIL FROM:", StringComparison.Ordinal))
                        await writer.WriteLineAsync("250 sender accepted");
                    else if (command.StartsWith("RCPT TO:", StringComparison.Ordinal))
                    {
                        recipients++;
                        await writer.WriteLineAsync(behavior == ServerBehavior.RejectSecondRecipient && recipients == 2
                            ? "550 recipient rejected" : "250 recipient accepted");
                    }
                    else if (command == "DATA")
                    {
                        await writer.WriteLineAsync("354 send message");
                        var lines = new List<string>();
                        while (await reader.ReadLineAsync(lifetime.Token) is { } line && line != ".") lines.Add(line);
                        MessageText = string.Join("\r\n", lines);
                        DataSubmissions++;
                        if (behavior == ServerBehavior.DropAfterData) return;
                        await writer.WriteLineAsync(behavior == ServerBehavior.RejectData ? "550 message rejected" : "250 accepted");
                    }
                    else if (command == "QUIT") { await writer.WriteLineAsync("221 goodbye"); return; }
                    else if (command == "RSET") await writer.WriteLineAsync("250 reset");
                    else await writer.WriteLineAsync("500 unsupported");
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or AuthenticationException or SocketException) { }
        }

        public async ValueTask DisposeAsync()
        {
            await lifetime.CancelAsync();
            listener.Stop();
            await run;
            lifetime.Dispose();
            Certificate.Dispose();
        }
    }
}
