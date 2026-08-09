using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Ganss.Xss;
using MailWinnow.Infrastructure.Persistence;
using MailWinnow.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MailWinnow.Infrastructure.Mailboxes;

public sealed record InboxMessageSummary(uint Uid, uint UidValidity, string From, string Subject, DateTimeOffset? Date);
public sealed record InboxMessageContent(uint Uid, string From, string To, string Subject, DateTimeOffset? Date,
    string HtmlBody, string HtmlBodyWithRemoteImages, bool HasRemoteImages);
public sealed record InboxLoadResult<T>(bool Succeeded, T? Value, string? Error = null);

public interface IInboxReaderService
{
    Task<InboxLoadResult<int>> CountAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default);
    Task<InboxLoadResult<IReadOnlyList<InboxMessageSummary>>> ListAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default);
    Task<InboxLoadResult<InboxMessageContent>> ReadAsync(ClaimsPrincipal user, uint uid, uint uidValidity, CancellationToken cancellationToken = default);
}

public sealed partial class InboxReaderService(
    MailWinnowDbContext db,
    IOwnershipAuthorizer ownership,
    ICredentialProtectionService credentials,
    IOptions<LocalImapOptions> options,
    IImapConnectionService imap) : IInboxReaderService
{
    public async Task<InboxLoadResult<int>> CountAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        var destination = await DestinationAsync(user, cancellationToken);
        if (destination is null) return new(false, 0, "Configure and enable a destination mailbox first.");
        var snapshot = await imap.GetFolderSnapshotAsync(Connection(destination), destination.Folder, cancellationToken);
        return snapshot.Succeeded && snapshot.Value is not null
            ? new(true, snapshot.Value.Uids.Count)
            : new(false, 0, snapshot.Error ?? "Unable to read the destination mailbox.");
    }

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
        var html = !string.IsNullOrWhiteSpace(message.HtmlBody) ? message.HtmlBody : PlainTextHtml(message.TextBody ?? string.Empty);
        html = ResolveEmbeddedImages(message, html);
        var sanitized = Sanitize(html);
        var hasRemoteImages = RemoteImageRegex().IsMatch(sanitized);
        return new(true, new(uid, message.From.ToString(), message.To.ToString(), message.Subject ?? "(no subject)", message.Date,
            WrapDocument(BlockRemoteImages(sanitized), false), WrapDocument(sanitized, true), hasRemoteImages));
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
    private static string PlainTextHtml(string text)
    {
        return $"<div style=\"white-space:pre-wrap\">{WebUtility.HtmlEncode(text)}</div>";
    }

    private static string Sanitize(string html)
    {
        var sanitizer = new HtmlSanitizer();
        sanitizer.AllowedSchemes.Add("data");
        sanitizer.AllowedAttributes.Add("class");
        sanitizer.AllowedAttributes.Add("id");
        return sanitizer.Sanitize(html);
    }

    private static string ResolveEmbeddedImages(MimeKit.MimeMessage message, string html)
    {
        foreach (var part in message.BodyParts.OfType<MimeKit.MimePart>())
        {
            if (string.IsNullOrWhiteSpace(part.ContentId) || part.Content is null || !SafeEmbeddedImageTypes.Contains(part.ContentType.MimeType)) continue;
            using var stream = new MemoryStream();
            part.Content.DecodeTo(stream);
            if (stream.Length > MaxEmbeddedImageBytes) continue;
            var dataUri = $"data:{part.ContentType.MimeType};base64,{Convert.ToBase64String(stream.ToArray())}";
            html = Regex.Replace(html, $"cid:{Regex.Escape(part.ContentId.Trim('<', '>'))}", dataUri,
                RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        }
        return html;
    }

    private static string BlockRemoteImages(string html) => RemoteImageRegex().Replace(html, match =>
        $"{match.Groups["prefix"].Value}{TransparentPixel}{match.Groups["suffix"].Value}");

    private static string WrapDocument(string body, bool allowRemoteImages)
    {
        var imageSource = allowRemoteImages ? "https: http: data:" : "data:";
        return $$"""
            <!doctype html><html><head><meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src {{imageSource}}; style-src 'unsafe-inline'; font-src data:">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <style>html,body{margin:0;padding:0;max-width:100%;overflow-wrap:anywhere}img{max-width:100%;height:auto}table{max-width:100%}a{word-break:break-word}</style>
            </head><body>{{body}}</body></html>
            """;
    }

    private const int MaxEmbeddedImageBytes = 10 * 1024 * 1024;
    private const string TransparentPixel = "data:image/gif;base64,R0lGODlhAQABAAD/ACwAAAAAAQABAAACADs=";
    private static readonly HashSet<string> SafeEmbeddedImageTypes = new(StringComparer.OrdinalIgnoreCase)
        { "image/gif", "image/jpeg", "image/png", "image/webp" };

    [GeneratedRegex("(?<prefix><img\\b[^>]*?\\bsrc\\s*=\\s*[\\\"'])(?:https?:)?//[^\\\"']+(?<suffix>[\\\"'])", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RemoteImageRegex();
}
