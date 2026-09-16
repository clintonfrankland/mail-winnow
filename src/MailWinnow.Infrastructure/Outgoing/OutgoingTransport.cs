using System.Net.Sockets;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace MailWinnow.Infrastructure.Outgoing;

public sealed record SendingConnection(string Host, int Port, bool UseStartTls, bool UseAuthentication, string Username, string Password);
public enum SubmissionOutcome { Accepted, Failed, OutcomeUnknown }
public sealed record SubmissionResult(SubmissionOutcome Outcome, string? FailureCode = null, bool Retryable = false);
public interface IOutgoingTransport
{
    Task<SubmissionResult> SubmitAsync(SendingConnection connection, MimeMessage message, CancellationToken cancellationToken);
}
/// <summary>Any uncertainty once SendAsync starts is held for a human; a stable Message-ID is not an exactly-once guarantee.</summary>
public sealed class SmtpOutgoingTransport : IOutgoingTransport
{
    private readonly Func<SmtpClient> createClient;
    public SmtpOutgoingTransport() : this(() => new SmtpClient()) { }
    public SmtpOutgoingTransport(Func<SmtpClient> createClient) => this.createClient = createClient;
    public async Task<SubmissionResult> SubmitAsync(SendingConnection connection, MimeMessage message, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        using var client = createClient();
        client.Timeout = 120000;
        var submissionStarted = false;
        try
        {
            await client.ConnectAsync(connection.Host, connection.Port,
                connection.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.SslOnConnect, timeout.Token);
            if (connection.UseAuthentication)
                await client.AuthenticateAsync(connection.Username, connection.Password, timeout.Token);
            submissionStarted = true;
            // MailKit's default recipient-rejection handler throws before DATA: no partial sends.
            await client.SendAsync(message, timeout.Token);
            // Do not let a QUIT failure overwrite SMTP acceptance.
            return new(SubmissionOutcome.Accepted);
        }
        catch (SmtpCommandException)
        {
            // An explicit SMTP rejection is definitive even after DATA. Never log the response/recipient.
            return new(SubmissionOutcome.Failed, "SmtpRejected", false);
        }
        catch (AuthenticationException) { return new(SubmissionOutcome.Failed, "AuthenticationFailed"); }
        catch (Exception exception) when (exception is IOException or SocketException or TimeoutException or OperationCanceledException or SmtpProtocolException)
        {
            return submissionStarted ? new(SubmissionOutcome.OutcomeUnknown, "SubmissionOutcomeUnknown")
                : new(SubmissionOutcome.Failed, "ConnectionFailed", true);
        }
        catch (Exception)
        {
            return submissionStarted ? new(SubmissionOutcome.OutcomeUnknown, "SubmissionOutcomeUnknown")
                : new(SubmissionOutcome.Failed, "ConfigurationFailed");
        }
    }
}
