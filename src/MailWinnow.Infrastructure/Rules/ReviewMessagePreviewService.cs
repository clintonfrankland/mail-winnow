using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Ganss.Xss;
using MailWinnow.Core.Rules;
using MailWinnow.Infrastructure.Mailboxes;
using MailWinnow.Infrastructure.Persistence;
using MailWinnow.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace MailWinnow.Infrastructure.Rules;

public sealed record ReviewMessagePreview(string From, string To, string Subject, DateTimeOffset ReceivedUtc, string HtmlBody);
public sealed record ReviewPreviewResult(bool Succeeded, ReviewMessagePreview? Value, string? Error = null);

public interface IReviewMessagePreviewService
{
    Task<ReviewPreviewResult> ReadAsync(ClaimsPrincipal user, Guid reviewItemId, CancellationToken cancellationToken = default);
}

/// <summary>Reads a single catalogued pending source message without changing review or delivery state.</summary>
public sealed partial class ReviewMessagePreviewService(MailWinnowDbContext db, IOwnershipAuthorizer ownership,
    ICredentialProtectionService credentials, IImapConnectionService imap, IMessageReviewService reviews) : IReviewMessagePreviewService
{
    public async Task<ReviewPreviewResult> ReadAsync(ClaimsPrincipal user, Guid reviewItemId, CancellationToken cancellationToken = default)
    {
        var owner = ownership.RequireCurrentUserId(user);
        // Do not turn this endpoint into a generic source-mail reader: only a current pending review item is previewable.
        var isPendingReviewItem = (await reviews.GetRecentAsync(user, new MessageReviewFilter(null, RuleOutcome.Pending, null), cancellationToken))
            .Any(item => item.Id == reviewItemId);
        if (!isPendingReviewItem) return new(false, null, "This message is no longer available for preview.");
        var found = await (from header in db.SourceMessageHeaders.AsNoTracking()
                           join source in db.SourceMailboxes.AsNoTracking() on header.SourceMailboxId equals source.Id
                           where header.Id == reviewItemId && source.OwnerUserId == owner
                           select new { header, source }).SingleOrDefaultAsync(cancellationToken);
        if (found is null) return new(false, null, "This message is no longer available for preview.");
        try
        {
            var connection = new ImapConnectionSettings(found.source.Host, found.source.Port, found.source.UseSsl, found.source.Username,
                credentials.Unprotect(found.source.ProtectedCredential, CredentialKind.SourceImapPassword));
            // The stored folder, UID and UIDVALIDITY are the only source identity used for the fetch.
            var result = await imap.FetchMessageAsync(connection, found.header.FolderName, found.header.Uid, found.header.UidValidity, cancellationToken);
            if (!result.Succeeded || result.Value is null) return new(false, null, "The source message could not be loaded. Try again after the next mailbox sync.");
            var message = result.Value;
            var html = string.IsNullOrWhiteSpace(message.HtmlBody) ? PlainTextHtml(message.TextBody ?? string.Empty) : message.HtmlBody;
            html = ResolveEmbeddedImages(message, html);
            return new(true, new(message.From.ToString(), message.To.ToString(), message.Subject ?? "(no subject)",
                message.Date == default ? found.header.ReceivedUtc : message.Date, WrapDocument(BlockRemoteImages(Sanitize(html)))));
        }
        catch (Exception)
        {
            return new(false, null, "The source message could not be loaded. Check the mailbox connection and try again.");
        }
    }

    // Use a semantic preformatted element because the sanitizer intentionally removes inline styles.
    private static string PlainTextHtml(string text) => $"<pre>{WebUtility.HtmlEncode(text)}</pre>";
    private static string Sanitize(string html)
    {
        var sanitizer = new HtmlSanitizer(); sanitizer.AllowedSchemes.Add("data"); sanitizer.AllowedAttributes.Add("class"); sanitizer.AllowedAttributes.Add("id");
        sanitizer.PostProcessNode += (_, args) => { if (args.Node is IElement { LocalName: "a" } link && link.HasAttribute("href")) { link.SetAttribute("target", "_blank"); link.SetAttribute("rel", "noopener noreferrer"); } };
        return sanitizer.Sanitize(html);
    }
    private static string ResolveEmbeddedImages(MimeKit.MimeMessage message, string html)
    {
        foreach (var part in message.BodyParts.OfType<MimeKit.MimePart>())
        { if (string.IsNullOrWhiteSpace(part.ContentId) || part.Content is null || !SafeImageTypes.Contains(part.ContentType.MimeType)) continue; using var stream = new MemoryStream(); part.Content.DecodeTo(stream); if (stream.Length > 10 * 1024 * 1024) continue; html = Regex.Replace(html, $"cid:{Regex.Escape(part.ContentId.Trim('<', '>'))}", $"data:{part.ContentType.MimeType};base64,{Convert.ToBase64String(stream.ToArray())}", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)); }
        return html;
    }
    private static string BlockRemoteImages(string html) => RemoteImageRegex().Replace(html, "${prefix}data:image/gif;base64,R0lGODlhAQABAAD/ACwAAAAAAQABAAACADs=${suffix}");
    private static string WrapDocument(string body) => $$"""<!doctype html><html><head><meta charset="utf-8"><meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src data:; style-src 'unsafe-inline'; font-src data:"><meta name="referrer" content="no-referrer"><meta name="viewport" content="width=device-width,initial-scale=1"><style>html,body{margin:0;padding:0;max-width:100%;overflow-wrap:anywhere}img,table{max-width:100%;height:auto}a{word-break:break-word}</style></head><body>{{body}}</body></html>""";
    private static readonly HashSet<string> SafeImageTypes = new(StringComparer.OrdinalIgnoreCase) { "image/gif", "image/jpeg", "image/png", "image/webp" };
    [GeneratedRegex("(?<prefix><img\\b[^>]*?\\bsrc\\s*=\\s*[\\\"'])(?:https?:)?//[^\\\"']+(?<suffix>[\\\"'])", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)] private static partial Regex RemoteImageRegex();
}
