using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Ganss.Xss;
using MailWinnow.Infrastructure.Persistence;
using MailWinnow.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MailWinnow.Infrastructure.Mailboxes;

public sealed record InboxMessageSummary(uint Uid, uint UidValidity, string From, string Subject, DateTimeOffset Date,
    string? RetentionLabel = null);
public sealed record InboxMessageContent(uint Uid, string From, string To, string Subject, DateTimeOffset Date,
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
        var trackedDeliveries = await db.MessageDeliveries.AsNoTracking()
            .Where(x => x.OwnerUserId == destination.OwnerUserId && x.DestinationMailboxId == destination.Id &&
                x.DestinationFolder == destination.Folder && x.DestinationUidValidity == snapshot.Value.UidValidity &&
                x.DestinationUid != null && uids.Contains(x.DestinationUid.Value) && x.State == MessageDeliveryState.Delivered)
            .Select(x => new { Uid = x.DestinationUid!.Value, x.DeliveredUtc, x.ExpiresUtc })
            .ToArrayAsync(cancellationToken);
        var retentionByUid = trackedDeliveries.GroupBy(x => x.Uid)
            .Where(x => x.Count() == 1)
            .ToDictionary(x => x.Key, x => RetentionLabel(x.Single().DeliveredUtc, x.Single().ExpiresUtc));
        return new(true, headers.Value.Select(x => new InboxMessageSummary(x.Uid, snapshot.Value.UidValidity,
                x.From ?? "(unknown sender)", x.Subject ?? "(no subject)", ResolveDate(x), retentionByUid.GetValueOrDefault(x.Uid)))
            .OrderByDescending(x => x.Date).ThenByDescending(x => x.Uid).ToArray());
    }

    public static string? RetentionLabel(DateTimeOffset? deliveredUtc, DateTimeOffset? expiresUtc)
    {
        if (expiresUtc is null) return "Forever";
        if (deliveredUtc is null) return null;
        var days = (expiresUtc.Value - deliveredUtc.Value).TotalDays;
        return days switch
        {
            >= 29.999 and <= 30.001 => "1 month",
            >= 6.999 and <= 7.001 => "1 week",
            >= 2.999 and <= 3.001 => "3 days",
            >= .999 and <= 1.001 => "1 day",
            _ => null
        };
    }

    public async Task<InboxLoadResult<InboxMessageContent>> ReadAsync(ClaimsPrincipal user, uint uid, uint uidValidity, CancellationToken cancellationToken = default)
    {
        var destination = await DestinationAsync(user, cancellationToken);
        if (destination is null) return new(false, null, "Configure and enable a destination mailbox first.");
        var connection = Connection(destination);
        var header = await imap.FetchHeadersAsync(connection, destination.Folder, [uid], uidValidity, cancellationToken);
        var result = await imap.FetchMessageAsync(connection, destination.Folder, uid, uidValidity, cancellationToken);
        if (!result.Succeeded || result.Value is null) return new(false, null, result.Error ?? "Unable to read the message.");
        var message = result.Value;
        var html = !string.IsNullOrWhiteSpace(message.HtmlBody) ? message.HtmlBody : PlainTextHtml(message.TextBody ?? string.Empty);
        html = ResolveEmbeddedImages(message, html);
        var sanitized = Sanitize(html);
        var hasRemoteImages = RemoteImageRegex().IsMatch(sanitized);
        var date = header.Succeeded && header.Value?.SingleOrDefault() is { } fetchedHeader
            ? ResolveDate(fetchedHeader)
            : message.Date == default ? DateTimeOffset.UnixEpoch : message.Date;
        return new(true, new(uid, message.From.ToString(), message.To.ToString(), message.Subject ?? "(no subject)", date,
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
    private static DateTimeOffset ResolveDate(ImapMessageHeader header) =>
        header.Date ?? header.InternalDate ?? DateTimeOffset.UnixEpoch;
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
        sanitizer.PostProcessNode += (_, args) =>
        {
            if (args.Node is not IElement { LocalName: "a" } link || !link.HasAttribute("href")) return;
            link.SetAttribute("target", "_blank");
            link.SetAttribute("rel", "noopener noreferrer");
        };
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
