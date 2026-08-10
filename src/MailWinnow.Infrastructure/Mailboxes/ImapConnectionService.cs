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
    Task<ImapOperationResult<uint?>> AppendMessageAsync(ImapConnectionSettings connection, string folderName, MimeMessage message, CancellationToken cancellationToken = default, DateTimeOffset? receivedUtc = null);
    Task<ImapOperationResult<int>> DeleteAndExpungeAsync(ImapConnectionSettings connection, string folderName, IReadOnlyList<uint> expiredUids, uint expectedUidValidity, CancellationToken cancellationToken = default);
    Task<ImapOperationResult<int>> MoveToFolderAsync(ImapConnectionSettings connection, string sourceFolderName, IReadOnlyList<uint> uids, uint expectedUidValidity, string destinationFolderName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task<ImapOperationResult<int>> DeleteOlderThanAsync(ImapConnectionSettings connection, string folderName, DateTimeOffset cutoffUtc, bool createFolderIfMissing = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task<ImapOperationResult<bool>> TestConnectionAsync(ImapConnectionSettings connection, CancellationToken cancellationToken = default);
    Task<ImapOperationResult<ImapFolderSnapshot>> GetFolderSnapshotAsync(ImapConnectionSettings connection, string folderName, CancellationToken cancellationToken = default);
}

public sealed record ImapConnectionSettings(string Host, int Port, bool UseSsl, string Username, string Password, TimeSpan? Timeout = null);
public sealed record ImapMessageHeader(uint Uid, string? MessageId, DateTimeOffset? Date, string? From, string? Sender, string? ReplyTo, string? To, string? Cc, string? Subject, string? AuthenticationResults, DateTimeOffset? InternalDate = null);
public sealed record ImapFolderSnapshot(uint UidValidity, IReadOnlyList<uint> Uids);
public enum ImapFailureKind { None, Authentication, Connection, Timeout, MissingFolder, UidValidityChanged, Throttled, Transient, Unexpected }
public sealed record ImapOperationResult<T>(bool Succeeded, T? Value, ImapFailureKind FailureKind = ImapFailureKind.None, string? Error = null)
{
    public static ImapOperationResult<T> Success(T value) => new(true, value);
    public static ImapOperationResult<T> Failure(ImapFailureKind kind, string error) => new(false, default, kind, error);
}

/// <summary>Transport selection that deliberately has no opportunistic/downgradeable TLS option.</summary>
public enum ImapTransportSecurity { SslOnConnect, StartTlsRequired }

/// <summary>Test seam for the IMAP protocol operations used by MailWinnow.</summary>
public interface IImapClientSession : IAsyncDisposable
{
    Task ConnectAsync(string host, int port, ImapTransportSecurity security, CancellationToken cancellationToken);
    Task AuthenticateAsync(string username, string password, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> ListFoldersAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ImapMessageHeader>> FetchHeadersAsync(string folderName, IReadOnlyList<uint> uids, uint? expectedUidValidity, CancellationToken cancellationToken);
    Task<MimeMessage> FetchMessageAsync(string folderName, uint uid, uint? expectedUidValidity, CancellationToken cancellationToken);
    Task<uint?> AppendMessageAsync(string folderName, MimeMessage message, CancellationToken cancellationToken, DateTimeOffset? receivedUtc = null);
    Task DeleteAndExpungeAsync(string folderName, IReadOnlyList<uint> uids, uint expectedUidValidity, CancellationToken cancellationToken);
    Task MoveToFolderAsync(string sourceFolderName, IReadOnlyList<uint> uids, uint expectedUidValidity, string destinationFolderName, CancellationToken cancellationToken) => throw new NotSupportedException();
    Task<int> DeleteOlderThanAsync(string folderName, DateTimeOffset cutoffUtc, bool createFolderIfMissing, CancellationToken cancellationToken) => throw new NotSupportedException();
    Task DisconnectAsync(CancellationToken cancellationToken);
    Task<ImapFolderSnapshot> GetFolderSnapshotAsync(string folderName, CancellationToken cancellationToken);
}

public interface IImapClientSessionFactory { IImapClientSession Create(); }

public sealed class ImapClientSessionFactory : IImapClientSessionFactory
{
    public IImapClientSession Create() => new MailKitImapClientSession();
}

public sealed class ImapConnectionService(IImapClientSessionFactory? sessions = null) : IImapConnectionService
{
    private readonly IImapClientSessionFactory _sessions = sessions ?? new ImapClientSessionFactory();

    public Task<ImapOperationResult<bool>> TestConnectionAsync(ImapConnectionSettings connection, CancellationToken cancellationToken = default) =>
        WithSessionAsync(connection, (_, _) => Task.FromResult(true), cancellationToken);

    public Task<ImapOperationResult<ImapFolderSnapshot>> GetFolderSnapshotAsync(ImapConnectionSettings connection, string folderName, CancellationToken cancellationToken = default) =>
        WithSessionAsync(connection, (session, token) => session.GetFolderSnapshotAsync(folderName, token), cancellationToken);

    public Task<ImapOperationResult<IReadOnlyList<string>>> ListFoldersAsync(ImapConnectionSettings connection, CancellationToken cancellationToken = default) =>
        WithSessionAsync(connection, (session, token) => session.ListFoldersAsync(token), cancellationToken);

    public Task<ImapOperationResult<IReadOnlyList<ImapMessageHeader>>> FetchHeadersAsync(ImapConnectionSettings connection, string folderName, IReadOnlyList<uint> uids, uint? expectedUidValidity = null, CancellationToken cancellationToken = default)
    {
        var ids = uids.Where(x => x > 0).Distinct().ToArray();
        return ids.Length == 0
            ? Task.FromResult(ImapOperationResult<IReadOnlyList<ImapMessageHeader>>.Success([]))
            : WithSessionAsync(connection, (session, token) => session.FetchHeadersAsync(folderName, ids, expectedUidValidity, token), cancellationToken);
    }

    public Task<ImapOperationResult<MimeMessage>> FetchMessageAsync(ImapConnectionSettings connection, string folderName, uint uid, uint? expectedUidValidity = null, CancellationToken cancellationToken = default) =>
        WithSessionAsync(connection, (session, token) => session.FetchMessageAsync(folderName, uid, expectedUidValidity, token), cancellationToken);

    public Task<ImapOperationResult<uint?>> AppendMessageAsync(ImapConnectionSettings connection, string folderName, MimeMessage message, CancellationToken cancellationToken = default, DateTimeOffset? receivedUtc = null) =>
        WithSessionAsync(connection, (session, token) => session.AppendMessageAsync(folderName, message, token, receivedUtc), cancellationToken);

    public Task<ImapOperationResult<int>> DeleteAndExpungeAsync(ImapConnectionSettings connection, string folderName, IReadOnlyList<uint> expiredUids, uint expectedUidValidity, CancellationToken cancellationToken = default)
    {
        var ids = expiredUids.Where(x => x > 0).Distinct().ToArray();
        return ids.Length == 0
            ? Task.FromResult(ImapOperationResult<int>.Success(0))
            : WithSessionAsync(connection, async (session, token) => { await session.DeleteAndExpungeAsync(folderName, ids, expectedUidValidity, token); return ids.Length; }, cancellationToken);
    }

    public Task<ImapOperationResult<int>> MoveToFolderAsync(ImapConnectionSettings connection, string sourceFolderName, IReadOnlyList<uint> uids, uint expectedUidValidity, string destinationFolderName, CancellationToken cancellationToken = default)
    {
        var ids = uids.Where(x => x > 0).Distinct().ToArray();
        return ids.Length == 0
            ? Task.FromResult(ImapOperationResult<int>.Success(0))
            : WithSessionAsync(connection, async (session, token) => { await session.MoveToFolderAsync(sourceFolderName, ids, expectedUidValidity, destinationFolderName, token); return ids.Length; }, cancellationToken);
    }

    public Task<ImapOperationResult<int>> DeleteOlderThanAsync(ImapConnectionSettings connection, string folderName, DateTimeOffset cutoffUtc, bool createFolderIfMissing = false, CancellationToken cancellationToken = default) =>
        WithSessionAsync(connection, (session, token) => session.DeleteOlderThanAsync(folderName, cutoffUtc, createFolderIfMissing, token), cancellationToken);

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
            ImapFailureKind.Authentication => "IMAP authentication was rejected.", ImapFailureKind.MissingFolder => "The requested IMAP folder does not exist.",
            ImapFailureKind.UidValidityChanged => "The IMAP folder was reset; synchronization must restart.", ImapFailureKind.Timeout => "The IMAP operation timed out.",
            ImapFailureKind.Throttled => "The IMAP provider temporarily throttled this mailbox.", ImapFailureKind.Transient => "A temporary IMAP network failure occurred.",
            ImapFailureKind.Connection => "Unable to establish a secure IMAP connection.", _ => "The IMAP operation could not be completed."
        };
        return ImapOperationResult<T>.Failure(kind, message);
    }

    private async Task<ImapOperationResult<T>> WithSessionAsync<T>(ImapConnectionSettings connection, Func<IImapClientSession, CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(connection.Timeout ?? TimeSpan.FromSeconds(30));
        await using var session = _sessions.Create();
        try
        {
            await session.ConnectAsync(connection.Host, connection.Port, connection.UseSsl ? ImapTransportSecurity.SslOnConnect : ImapTransportSecurity.StartTlsRequired, timeout.Token);
            await session.AuthenticateAsync(connection.Username, connection.Password, timeout.Token);
            var result = await operation(session, timeout.Token);
            await session.DisconnectAsync(timeout.Token);
            return ImapOperationResult<T>.Success(result);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return ImapOperationResult<T>.Failure(ImapFailureKind.Timeout, "The IMAP operation timed out."); }
        catch (Exception exception) { return Failure<T>(exception, cancellationToken); }
    }

    internal sealed class UidValidityChangedException : Exception { }
}

internal sealed class MailKitImapClientSession : IImapClientSession
{
    private static readonly string[] HeaderFields = ["Message-ID", "Date", "From", "Sender", "Reply-To", "To", "Cc", "Subject", "Authentication-Results"];
    private readonly ImapClient _client = new();

    public Task ConnectAsync(string host, int port, ImapTransportSecurity security, CancellationToken cancellationToken) =>
        _client.ConnectAsync(host, port, security == ImapTransportSecurity.SslOnConnect ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls, cancellationToken);
    public Task AuthenticateAsync(string username, string password, CancellationToken cancellationToken) => _client.AuthenticateAsync(username, password, cancellationToken);
    public async Task<IReadOnlyList<string>> ListFoldersAsync(CancellationToken cancellationToken)
    {
        var ns = _client.PersonalNamespaces.FirstOrDefault();
        if (ns is null) return [];
        return (await _client.GetFoldersAsync(ns, StatusItems.None, false, cancellationToken)).Select(x => x.FullName).Order(StringComparer.Ordinal).ToArray();
    }
    public async Task<IReadOnlyList<ImapMessageHeader>> FetchHeadersAsync(string folderName, IReadOnlyList<uint> uids, uint? expectedUidValidity, CancellationToken cancellationToken)
    {
        var folder = await OpenFolderAsync(folderName, FolderAccess.ReadOnly, expectedUidValidity, cancellationToken);
        // The MailKit request includes RFC headers only, never a body section.
        var summaries = await folder.FetchAsync(uids.Select(x => new UniqueId(x)).ToArray(), MessageSummaryItems.UniqueId | MessageSummaryItems.Headers | MessageSummaryItems.InternalDate, HeaderFields, cancellationToken);
        return summaries.Select(x => new ImapMessageHeader(x.UniqueId.Id, Header(x, "Message-ID"), ParseDate(Header(x, "Date")), Header(x, "From"), Header(x, "Sender"), Header(x, "Reply-To"), Header(x, "To"), Header(x, "Cc"), Header(x, "Subject"), Header(x, "Authentication-Results"), x.InternalDate)).ToArray();
    }
    public async Task<MimeMessage> FetchMessageAsync(string folderName, uint uid, uint? expectedUidValidity, CancellationToken cancellationToken) => await (await OpenFolderAsync(folderName, FolderAccess.ReadOnly, expectedUidValidity, cancellationToken)).GetMessageAsync(new UniqueId(uid), cancellationToken);
    public async Task<uint?> AppendMessageAsync(string folderName, MimeMessage message, CancellationToken cancellationToken, DateTimeOffset? receivedUtc = null)
    {
        var folder = await GetOrCreateFolderAsync(folderName, cancellationToken);
        await folder.OpenAsync(FolderAccess.ReadWrite, cancellationToken);
        var uid = receivedUtc is { } date
            ? await folder.AppendAsync(message, MessageFlags.None, date, cancellationToken)
            : await folder.AppendAsync(message, MessageFlags.None, cancellationToken);
        return uid?.Id;
    }
    public async Task DeleteAndExpungeAsync(string folderName, IReadOnlyList<uint> uids, uint expectedUidValidity, CancellationToken cancellationToken)
    {
        var folder = await OpenFolderAsync(folderName, FolderAccess.ReadWrite, expectedUidValidity, cancellationToken);
        var ids = uids.Select(x => new UniqueId(x)).ToArray();
        await folder.AddFlagsAsync(ids, MessageFlags.Deleted, true, cancellationToken);
        await folder.ExpungeAsync(ids, cancellationToken); // UID EXPUNGE is restricted to the supplied IDs.
    }
    public async Task MoveToFolderAsync(string sourceFolderName, IReadOnlyList<uint> uids, uint expectedUidValidity, string destinationFolderName, CancellationToken cancellationToken)
    {
        var source = await OpenFolderAsync(sourceFolderName, FolderAccess.ReadWrite, expectedUidValidity, cancellationToken);
        var destination = await GetOrCreateFolderAsync(destinationFolderName, cancellationToken);
        await source.MoveToAsync(uids.Select(x => new UniqueId(x)).ToArray(), destination, cancellationToken);
    }
    public async Task<int> DeleteOlderThanAsync(string folderName, DateTimeOffset cutoffUtc, bool createFolderIfMissing, CancellationToken cancellationToken)
    {
        IMailFolder folder;
        try { folder = await _client.GetFolderAsync(folderName, cancellationToken); }
        catch (FolderNotFoundException) when (createFolderIfMissing) { folder = await GetOrCreateFolderAsync(folderName, cancellationToken); }
        await folder.OpenAsync(FolderAccess.ReadWrite, cancellationToken);
        var summaries = await folder.FetchAsync(0, -1, MessageSummaryItems.UniqueId | MessageSummaryItems.InternalDate, cancellationToken);
        var expired = summaries.Where(x => x.InternalDate is { } date && date < cutoffUtc).Select(x => x.UniqueId).ToArray();
        if (expired.Length == 0) return 0;
        await folder.AddFlagsAsync(expired, MessageFlags.Deleted, true, cancellationToken);
        await folder.ExpungeAsync(expired, cancellationToken);
        return expired.Length;
    }
    public Task DisconnectAsync(CancellationToken cancellationToken) => _client.IsConnected ? _client.DisconnectAsync(true, cancellationToken) : Task.CompletedTask;
    public async Task<ImapFolderSnapshot> GetFolderSnapshotAsync(string folderName, CancellationToken cancellationToken)
    {
        var folder = await OpenFolderAsync(folderName, FolderAccess.ReadOnly, null, cancellationToken);
        var uids = await folder.SearchAsync(MailKit.Search.SearchQuery.All, cancellationToken);
        return new(folder.UidValidity, uids.Select(x => x.Id).ToArray());
    }
    public ValueTask DisposeAsync() { _client.Dispose(); return ValueTask.CompletedTask; }
    private async Task<IMailFolder> OpenFolderAsync(string name, FolderAccess access, uint? expectedUidValidity, CancellationToken token)
    { var folder = await _client.GetFolderAsync(name, token); await folder.OpenAsync(access, token); if (expectedUidValidity is > 0 && folder.UidValidity != expectedUidValidity) throw new ImapConnectionService.UidValidityChangedException(); return folder; }
    private async Task<IMailFolder> GetOrCreateFolderAsync(string name, CancellationToken token)
    {
        try { return await _client.GetFolderAsync(name, token); }
        catch (FolderNotFoundException)
        {
            var ns = _client.PersonalNamespaces.FirstOrDefault() ?? throw new FolderNotFoundException(name);
            return await _client.GetFolder(ns).CreateAsync(name, true, token)
                ?? throw new FolderNotFoundException(name);
        }
    }
    private static string? Header(IMessageSummary summary, string name) => summary.Headers?[name];
    private static DateTimeOffset? ParseDate(string? value) => DateTimeOffset.TryParse(value, out var date) ? date : null;
}
