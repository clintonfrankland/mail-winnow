using MailKit.Security;
using MailWinnow.Infrastructure.Mailboxes;

namespace MailWinnow.Tests.Mailboxes;

public sealed class ImapConnectionServiceTests
{
    [Theory]
    [InlineData("timeout", ImapFailureKind.Timeout)]
    [InlineData("network", ImapFailureKind.Transient)]
    [InlineData("authentication", ImapFailureKind.Authentication)]
    public void ClassifyFailure_returns_sanitized_actionable_category(string failure, ImapFailureKind expected)
    {
        Exception exception = failure switch
        {
            "timeout" => new TimeoutException("host details must not escape"),
            "network" => new IOException("host details must not escape"),
            "authentication" => new AuthenticationException("credential details must not escape"),
            _ => throw new InvalidOperationException()
        };

        Assert.Equal(expected, ImapConnectionService.ClassifyFailure(exception));
    }

    [Fact]
    public void Header_contract_contains_only_requested_metadata()
    {
        var header = new ImapMessageHeader(42, "<id>", null, "from", "sender", "reply", "to", "cc", "subject", "pass");

        Assert.Equal((uint)42, header.Uid);
        Assert.Equal("pass", header.AuthenticationResults);
        Assert.DoesNotContain(typeof(ImapMessageHeader).GetProperties(), property => property.Name.Contains("Body", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Caller_cancellation_is_not_converted_to_a_mailbox_failure()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ImapConnectionService().TestConnectionAsync(
            new ImapConnectionSettings("imap.example.test", 993, true, "user", "password"), cancellation.Token));
    }
}
