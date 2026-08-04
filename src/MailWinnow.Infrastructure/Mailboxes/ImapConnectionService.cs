using System.Net.Sockets;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;

namespace MailWinnow.Infrastructure.Mailboxes;

/// <summary>Application-owned IMAP contract. Synchronization callers never need to depend on MailKit types.</summary>
public interface IImapConnectionService
{
    Task<ImapOperationResult<IReadOnlyList<string>>> ListFoldersAsync(ImapConnectionSettings connection, CancellationToken cancellationToken = default);
    Task<ImapOperationResult<IReadOnlyList<ImapMessageHeader>>> FetchHeadersAsync(ImapConnectionSettings connection, string folderName, IReadOnlyList<uint> uids, uint? expectedUidValidity = null, CancellationToken cancellationToken = default);
    Task<ImapOperationResult<MimeMessage>> FetchMessageAsync(ImapConnectionSettings connection, string folderName, uint uid, uint? expectedUidValidity = null, CancellationToken cancellationToken = default);
    Task<ImapOperationResult<uint?>> AppendMessageAsync(ImapConnectionSettings connection, string folderName, MimeMessage message, CancellationToken cancellationToken = default);
    Task<ImapOperationResult<int>> DeleteAndExpungeAsync(ImapConnectionSettings connection, string folderName, IReadOnlyList<uint> expiredUids, CancellationToken cancellationToken = default);
    Task<ImapOperationResult<bool>> TestConnectionAsync(ImapConnectionSettings connection, CancellationToken cancellationToken = default);
}

public sealed record ImapConnectionSettings(string Host, int Port, bool UseSsl, string Username, string Password, TimeSpan? Timeout = null);
public sealed record ImapMessageHeader(uint Uid, string? MessageId, DateTimeOffset? Date, string? From, string? Sender, string? ReplyTo, string? To, string? Cc, string? Subject, string? AuthenticationResults);
public enum ImapFailureKind { None, Authentication, Connection, Timeout, MissingFolder, UidValidityChanged, Throttled, Transient, Unexpected }
public sealed record ImapOperationResult<T>(bool Succeeded, T? Value, ImapFailureKind FailureKind = ImapFailureKind.None, string? Error = null)
{
    public static ImapOperationResult<T> Success(T value) => new(true, value);
    public static ImapOperationResult<T> Failure(ImapFailureKind kind, string error) => new(false, default, kind, error);
}

public sealed class ImapConnectionService : IImapConnectionService
{
    private static readonly string[] HeaderFields = ["Message-ID", "Date", "From", "Sender", "Reply-To", "To", "Cc", "Subject", "Authentication-Results"];

    public Task<ImapOperationResult<bool>> TestConnectionAsync(ImapConnectionSettings connection, CancellationToken cancellationToken = default) =>
        WithClientAsync(connection, async (client, token) => { _ = client.PersonalNamespaces; await Task.CompletedTask; return true; }, cancellationToken);

    public Task<ImapOperationResult<IReadOnlyList<string>>> ListFoldersAsync(ImapConnectionSettings connection, CancellationToken cancellationToken = default) =>
        WithClientAsync(connection, async (client, token) =>
        {
            var ns = client.PersonalNamespaces.FirstOrDefault();
            if (ns is null) return (IReadOnlyList<string>)[];
            var folders = await client.GetFoldersAsync(ns, StatusItems.None, false, token);
            return folders.Select(x => x.FullName).Order(StringComparer.Ordinal).ToArray();
        }, cancellationToken);

    public Task<ImapOperationResult<IReadOnlyList<ImapMessageHeader>>> FetchHeadersAsync(ImapConnectionSettings connection, string folderName, IReadOnlyList<uint> uids, uint? expectedUidValidity = null, CancellationToken cancellationToken = default) =>
        WithClientAsync(connection, async (client, token) =>
        {
            var folder = await OpenFolderAsync(client, folderName, FolderAccess.ReadOnly, expectedUidValidity, token);
            var ids = uids.Where(x => x > 0).Select(x => new UniqueId(x)).ToArray();
            if (ids.Length == 0) return (IReadOnlyList<ImapMessageHeader>)[];
            // This request explicitly asks for selected RFC headers only; it never requests a body.
            var summaries = await folder.FetchAsync(ids, MessageSummaryItems.UniqueId | MessageSummaryItems.Headers, HeaderFields, token);
            return summaries.Select(x => new ImapMessageHeader(x.UniqueId.Id, Header(x, "Message-ID"), ParseDate(Header(x, "Date")), Header(x, "From"), Header(x, "Sender"), Header(x, "Reply-To"), Header(x, "To"), Header(x, "Cc"), Header(x, "Subject"), Header(x, "Authentication-Results"))).ToArray();
        }, cancellationToken);

    public Task<ImapOperationResult<MimeMessage>> FetchMessageAsync(ImapConnectionSettings connection, string folderName, uint uid, uint? expectedUidValidity = null, CancellationToken cancellationToken = default) =>
        WithClientAsync(connection, async (client, token) =>
        {
            var folder = await OpenFolderAsync(client, folderName, FolderAccess.ReadOnly, expectedUidValidity, token);
            return await folder.GetMessageAsync(new UniqueId(uid), token);
        }, cancellationToken);

    public Task<ImapOperationResult<uint?>> AppendMessageAsync(ImapConnectionSettings connection, string folderName, MimeMessage message, CancellationToken cancellationToken = default) =>
        WithClientAsync(connection, async (client, token) =>
        {
            var folder = await OpenFolderAsync(client, folderName, FolderAccess.ReadWrite, null, token);
            var uid = await folder.AppendAsync(message, MessageFlags.None, token);
            return uid?.Id;
        }, cancellationToken);

    public Task<ImapOperationResult<int>> DeleteAndExpungeAsync(ImapConnectionSettings connection, string folderName, IReadOnlyList<uint> expiredUids, CancellationToken cancellationToken = default) =>
        WithClientAsync(connection, async (client, token) =>
        {
            var ids = expiredUids.Where(x => x > 0).Distinct().Select(x => new UniqueId(x)).ToArray();
            if (ids.Length == 0) return 0;
            var folder = await OpenFolderAsync(client, folderName, FolderAccess.ReadWrite, null, token);
            await folder.AddFlagsAsync(ids, MessageFlags.Deleted, true, token);
            // UID EXPUNGE limits removal to the explicit set; never expunge unrelated deleted mail.
            await folder.ExpungeAsync(ids, token);
            return ids.Length;
        }, cancellationToken);

    /// <summary>Maps provider failures to safe categories without retaining server response text.</summary>
    public static ImapFailureKind ClassifyFailure(Exception exception) => Failure<object>(exception, CancellationToken.None).FailureKind;

    internal static ImapOperationResult<T> Failure<T>(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested) throw exception;
        var kind = exception switch
        {
            UidValidityChangedException => ImapFailureKind.UidValidityChanged,
            AuthenticationException => ImapFailureKind.Authentication,
            FolderNotFoundException => ImapFailureKind.MissingFolder,
            TimeoutException => ImapFailureKind.Timeout,
            SocketException or IOException => ImapFailureKind.Transient,
            ImapCommandException when exception.Message.Contains("thrott", StringComparison.OrdinalIgnoreCase) || exception.Message.Contains("rate", StringComparison.OrdinalIgnoreCase) => ImapFailureKind.Throttled,
            ImapProtocolException => ImapFailureKind.Connection,
            _ => ImapFailureKind.Unexpected
        };
        var message = kind switch
        {
            ImapFailureKind.Authentication => "IMAP authentication was rejected.",
            ImapFailureKind.MissingFolder => "The requested IMAP folder does not exist.",
            ImapFailureKind.UidValidityChanged => "The IMAP folder was reset; synchronization must restart.",
            ImapFailureKind.Timeout => "The IMAP operation timed out.",
            ImapFailureKind.Throttled => "The IMAP provider temporarily throttled this mailbox.",
            ImapFailureKind.Transient => "A temporary IMAP network failure occurred.",
            ImapFailureKind.Connection => "Unable to establish a secure IMAP connection.",
            _ => "The IMAP operation could not be completed."
        };
        return ImapOperationResult<T>.Failure(kind, message);
    }

    private static async Task<ImapOperationResult<T>> WithClientAsync<T>(ImapConnectionSettings connection, Func<ImapClient, CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(connection.Timeout ?? TimeSpan.FromSeconds(30));
        try
        {
            using var client = new ImapClient { Timeout = (int)(connection.Timeout ?? TimeSpan.FromSeconds(30)).TotalMilliseconds };
            await client.ConnectAsync(connection.Host, connection.Port, connection.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable, timeout.Token);
            await client.AuthenticateAsync(connection.Username, connection.Password, timeout.Token);
            var result = await operation(client, timeout.Token);
            if (client.IsConnected) await client.DisconnectAsync(true, timeout.Token);
            return ImapOperationResult<T>.Success(result);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return ImapOperationResult<T>.Failure(ImapFailureKind.Timeout, "The IMAP operation timed out."); }
        catch (Exception exception) { return Failure<T>(exception, cancellationToken); }
    }

    private static async Task<IMailFolder> OpenFolderAsync(ImapClient client, string folderName, FolderAccess access, uint? expectedUidValidity, CancellationToken cancellationToken)
    {
        var folder = await client.GetFolderAsync(folderName, cancellationToken);
        await folder.OpenAsync(access, cancellationToken);
        if (expectedUidValidity is > 0 && folder.UidValidity != expectedUidValidity) throw new UidValidityChangedException();
        return folder;
    }

    private static string? Header(IMessageSummary summary, string name) => summary.Headers?[name];
    private static DateTimeOffset? ParseDate(string? value) => DateTimeOffset.TryParse(value, out var date) ? date : null;
    private sealed class UidValidityChangedException : Exception { }
}
