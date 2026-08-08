using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using MailWinnow.Infrastructure.Persistence;
using MailWinnow.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MailWinnow.Infrastructure.Mailboxes;

public sealed record InboxMessageSummary(uint Uid, uint UidValidity, string From, string Subject, DateTimeOffset? Date);
public sealed record InboxMessageContent(uint Uid, string From, string To, string Subject, DateTimeOffset? Date, string Body, IReadOnlyList<string> RemoteImageUrls);
public sealed record InboxLoadResult<T>(bool Succeeded, T? Value, string? Error = null);

public interface IInboxReaderService
{
    Task<InboxLoadResult<IReadOnlyList<InboxMessageSummary>>> ListAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default);
    Task<InboxLoadResult<InboxMessageContent>> ReadAsync(ClaimsPrincipal user, uint uid, uint uidValidity, CancellationToken cancellationToken = default);
}

public sealed class InboxReaderService(
    MailWinnowDbContext db,
    IOwnershipAuthorizer ownership,
    ICredentialProtectionService credentials,
    IOptions<LocalImapOptions> options,
    IImapConnectionService imap) : IInboxReaderService
{
    public async Task<InboxLoadResult<IReadOnlyList<InboxMessageSummary>>> ListAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        var destination = await DestinationAsync(user, cancellationToken);
        if (destination is null) return new(false, null, "Configure and enable a destination mailbox first.");
        var connection = Connection(destination);
        var snapshot = await imap.GetFolderSnapshotAsync(connection, destination.Folder, cancellationToken);
        if (!snapshot.Succeeded || snapshot.Value is null) return new(false, null, snapshot.Error ?? "Unable to read the destination mailbox.");
        var uids = snapshot.Value.Uids.OrderByDescending(x => x).Take(200).ToArray();
        var headers = await imap.FetchHeadersAsync(connection, destination.Folder, uids, snapshot.Value.UidValidity, cancellationToken);
        if (!headers.Succeeded || headers.Value is null) return new(false, null, headers.Error ?? "Unable to read message headers.");
        return new(true, headers.Value.OrderByDescending(x => x.Date).ThenByDescending(x => x.Uid)
            .Select(x => new InboxMessageSummary(x.Uid, snapshot.Value.UidValidity, x.From ?? "(unknown sender)", x.Subject ?? "(no subject)", x.Date)).ToArray());
    }

    public async Task<InboxLoadResult<InboxMessageContent>> ReadAsync(ClaimsPrincipal user, uint uid, uint uidValidity, CancellationToken cancellationToken = default)
    {
        var destination = await DestinationAsync(user, cancellationToken);
        if (destination is null) return new(false, null, "Configure and enable a destination mailbox first.");
        var result = await imap.FetchMessageAsync(Connection(destination), destination.Folder, uid, uidValidity, cancellationToken);
        if (!result.Succeeded || result.Value is null) return new(false, null, result.Error ?? "Unable to read the message.");
        var message = result.Value;
        var html = message.HtmlBody ?? string.Empty;
        var body = !string.IsNullOrWhiteSpace(message.TextBody) ? message.TextBody : ToPlainText(html);
        return new(true, new(uid, message.From.ToString(), message.To.ToString(), message.Subject ?? "(no subject)", message.Date, body,
            RemoteImages(html)));
    }

    private async Task<DestinationMailbox?> DestinationAsync(ClaimsPrincipal user, CancellationToken token)
    {
        var owner = ownership.RequireCurrentUserId(user);
        return await db.DestinationMailboxes.AsNoTracking().SingleOrDefaultAsync(x => x.OwnerUserId == owner && x.Enabled, token);
    }
    private ImapConnectionSettings Connection(DestinationMailbox destination)
    {
        var configured = options.Value;
        return new(configured.Host, configured.Port, configured.UseSsl, destination.Username,
            credentials.Unprotect(destination.ProtectedCredential, CredentialKind.DestinationImapPassword));
    }
    private static string ToPlainText(string html)
    {
        var withoutActive = Regex.Replace(html, "<(script|style)[^>]*>.*?</\\1>", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(1));
        var withLines = Regex.Replace(withoutActive, "</?(p|div|br|li|tr|h[1-6])[^>]*>", "\n", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        return WebUtility.HtmlDecode(Regex.Replace(withLines, "<[^>]+>", string.Empty, RegexOptions.Singleline, TimeSpan.FromSeconds(1))).Trim();
    }
    private static IReadOnlyList<string> RemoteImages(string html) => Regex.Matches(html, "<img[^>]+src\\s*=\\s*['\"](?<url>https?://[^'\"]+)['\"]", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1))
        .Select(x => WebUtility.HtmlDecode(x.Groups["url"].Value)).Where(x => Uri.TryCreate(x, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        .Distinct(StringComparer.Ordinal).Take(20).ToArray();
}
