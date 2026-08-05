using MailKit;
using MailKit.Net.Imap;
using MailWinnow.Infrastructure.Mailboxes;
using MimeKit;

namespace MailWinnow.Tests.Mailboxes;

public sealed class ImapConnectionServiceTests
{
    private static readonly ImapConnectionSettings Connection = new("imap.example.test", 143, false, "user", "password");

    [Fact]
    public async Task StartTls_is_required_and_authentication_is_not_attempted_when_unavailable()
    {
        var session = new ScriptedSession { ConnectException = new ImapProtocolException("STARTTLS unavailable") };
        var result = await new ImapConnectionService(new ScriptedFactory(session)).TestConnectionAsync(Connection);

        Assert.False(result.Succeeded);
        Assert.Equal(ImapFailureKind.Connection, result.FailureKind);
        Assert.Equal(["connect:StartTlsRequired"], session.Commands);
    }

    [Fact]
    public async Task Header_synchronization_uses_header_only_operation_without_full_message_fetch()
    {
        var session = new ScriptedSession { Headers = [new ImapMessageHeader(42, "<id>", null, "from", null, null, "to", null, "subject", "pass")] };
        var result = await new ImapConnectionService(new ScriptedFactory(session)).FetchHeadersAsync(Connection, "INBOX", [42]);

        Assert.True(result.Succeeded);
        Assert.Equal((uint)42, Assert.Single(result.Value!).Uid);
        Assert.Equal(["connect:StartTlsRequired", "authenticate", "fetch-headers:INBOX:42", "disconnect"], session.Commands);
        Assert.DoesNotContain(session.Commands, command => command.StartsWith("fetch-message", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Full_message_append_passes_the_original_message_to_destination()
    {
        var message = new MimeMessage { Subject = "keep the complete message" };
        var session = new ScriptedSession { AppendedUid = 99 };
        var result = await new ImapConnectionService(new ScriptedFactory(session)).AppendMessageAsync(Connection, "Archive", message);

        Assert.True(result.Succeeded);
        Assert.Equal((uint)99, result.Value);
        Assert.Same(message, session.AppendedMessage);
        Assert.Equal(["connect:StartTlsRequired", "authenticate", "append:Archive", "disconnect"], session.Commands);
    }

    [Fact]
    public async Task Delete_and_expunge_passes_only_distinct_requested_uids()
    {
        var session = new ScriptedSession();
        var result = await new ImapConnectionService(new ScriptedFactory(session)).DeleteAndExpungeAsync(Connection, "Archive", [4, 4, 9, 0]);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Value);
        Assert.Equal([4u, 9u], session.DeletedUids);
        Assert.Equal(["connect:StartTlsRequired", "authenticate", "delete-expunge:Archive:4,9", "disconnect"], session.Commands);
    }

    [Fact]
    public async Task Transient_operational_failure_is_sanitized_and_cancellation_is_forwarded()
    {
        var session = new ScriptedSession { HeaderException = new IOException("private host details") };
        var result = await new ImapConnectionService(new ScriptedFactory(session)).FetchHeadersAsync(Connection, "INBOX", [1]);
        Assert.False(result.Succeeded);
        Assert.Equal(ImapFailureKind.Transient, result.FailureKind);
        Assert.DoesNotContain("private host details", result.Error!);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ImapConnectionService(new ScriptedFactory(new ScriptedSession())).TestConnectionAsync(Connection, cancellation.Token));
    }

    private sealed class ScriptedFactory(ScriptedSession session) : IImapClientSessionFactory { public IImapClientSession Create() => session; }
    private sealed class ScriptedSession : IImapClientSession
    {
        public List<string> Commands { get; } = [];
        public Exception? ConnectException { get; init; }
        public Exception? HeaderException { get; init; }
        public IReadOnlyList<ImapMessageHeader> Headers { get; init; } = [];
        public uint? AppendedUid { get; init; }
        public MimeMessage? AppendedMessage { get; private set; }
        public IReadOnlyList<uint> DeletedUids { get; private set; } = [];
        public Task ConnectAsync(string host, int port, ImapTransportSecurity security, CancellationToken token) { token.ThrowIfCancellationRequested(); Commands.Add($"connect:{security}"); if (ConnectException is not null) throw ConnectException; return Task.CompletedTask; }
        public Task AuthenticateAsync(string username, string password, CancellationToken token) { Commands.Add("authenticate"); return Task.CompletedTask; }
        public Task<IReadOnlyList<string>> ListFoldersAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<ImapMessageHeader>> FetchHeadersAsync(string folder, IReadOnlyList<uint> uids, uint? uidValidity, CancellationToken token) { Commands.Add($"fetch-headers:{folder}:{string.Join(',', uids)}"); if (HeaderException is not null) throw HeaderException; return Task.FromResult(Headers); }
        public Task<MimeMessage> FetchMessageAsync(string folder, uint uid, uint? uidValidity, CancellationToken token) => Task.FromResult(new MimeMessage());
        public Task<uint?> AppendMessageAsync(string folder, MimeMessage message, CancellationToken token, DateTimeOffset? receivedUtc = null) { Commands.Add($"append:{folder}"); AppendedMessage = message; return Task.FromResult(AppendedUid); }
        public Task DeleteAndExpungeAsync(string folder, IReadOnlyList<uint> uids, CancellationToken token) { Commands.Add($"delete-expunge:{folder}:{string.Join(',', uids)}"); DeletedUids = uids; return Task.CompletedTask; }
        public Task DisconnectAsync(CancellationToken token) { Commands.Add("disconnect"); return Task.CompletedTask; }
        public Task<ImapFolderSnapshot> GetFolderSnapshotAsync(string folderName, CancellationToken token) => Task.FromResult(new ImapFolderSnapshot(1, []));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
