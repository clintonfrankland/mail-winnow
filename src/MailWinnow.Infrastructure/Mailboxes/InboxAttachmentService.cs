using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MailWinnow.Infrastructure.Persistence;
using MailWinnow.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimeKit;

namespace MailWinnow.Infrastructure.Mailboxes;

public sealed record InboxAttachment(int Index, string FileName, string ContentType);
public enum InboxAttachmentDownloadStatus { Success, NotFound, TooLarge, Unavailable }
public sealed record InboxAttachmentDownloadResult(InboxAttachmentDownloadStatus Status, Stream? Content = null, string? FileName = null);

public interface IInboxAttachmentService
{
    Task<InboxAttachmentDownloadResult> DownloadAsync(ClaimsPrincipal user, Guid destinationMailboxId,
        string destinationFolder, string mailboxIdentity, uint uid, uint uidValidity, int attachmentIndex, CancellationToken cancellationToken = default);
}

/// <summary>Reads owner-scoped MIME attachments without changing mailbox flags or message state.</summary>
public sealed class InboxAttachmentService(MailWinnowDbContext db, IOwnershipAuthorizer ownership,
    ICredentialProtectionService credentials, IOptions<LocalImapOptions> options, IImapConnectionService imap)
    : IInboxAttachmentService
{
    public const int MaxAttachmentBytes = 25 * 1024 * 1024;

    public async Task<InboxAttachmentDownloadResult> DownloadAsync(ClaimsPrincipal user, Guid destinationMailboxId,
        string destinationFolder, string mailboxIdentity, uint uid, uint uidValidity, int attachmentIndex, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var owner = ownership.RequireCurrentUserId(user);
        if (destinationMailboxId == Guid.Empty || uid == 0 || uidValidity == 0 || attachmentIndex < 0)
            return new(InboxAttachmentDownloadStatus.NotFound);
        var destination = await db.DestinationMailboxes.AsNoTracking().SingleOrDefaultAsync(
            mailbox => mailbox.OwnerUserId == owner && mailbox.Id == destinationMailboxId && mailbox.Enabled, cancellationToken);
        // Compare ordinally in memory as database collation may ignore folder-name case.
        if (destination is null || !string.Equals(destination.Folder, destinationFolder, StringComparison.Ordinal))
            return new(InboxAttachmentDownloadStatus.NotFound);
        var configuration = options.Value;
        if (!string.Equals(mailboxIdentity, MailboxIdentity(destination.Id, destination.Username, destination.Folder, configuration), StringComparison.Ordinal))
            return new(InboxAttachmentDownloadStatus.NotFound);
        var connection = new ImapConnectionSettings(configuration.Host, configuration.Port, configuration.UseSsl,
            destination.Username, credentials.Unprotect(destination.ProtectedCredential, CredentialKind.DestinationImapPassword));
        var fetched = await imap.FetchMessageAsync(connection, destination.Folder, uid, uidValidity, cancellationToken);
        if (!fetched.Succeeded || fetched.Value is null)
            return new(fetched.FailureKind is ImapFailureKind.UidValidityChanged or ImapFailureKind.MissingFolder or ImapFailureKind.MissingMessage
                ? InboxAttachmentDownloadStatus.NotFound : InboxAttachmentDownloadStatus.Unavailable);
        using var message = fetched.Value;
        var attachment = EnumerateAttachments(message.Body).Skip(attachmentIndex).FirstOrDefault();
        if (attachment is null) return new(InboxAttachmentDownloadStatus.NotFound);
        var stream = new BoundedAttachmentStream(MaxAttachmentBytes);
        try
        {
            if (attachment is MimePart { Content: not null } part)
                await part.Content.DecodeToAsync(stream, cancellationToken);
            else if (attachment is MessagePart { Message: not null } attachedMessage)
                await attachedMessage.Message.WriteToAsync(stream, cancellationToken);
            else
            {
                await stream.DisposeAsync();
                return new(InboxAttachmentDownloadStatus.NotFound);
            }
            cancellationToken.ThrowIfCancellationRequested();
            stream.Position = 0;
            return new(InboxAttachmentDownloadStatus.Success, stream, AttachmentFileName(attachment, attachmentIndex));
        }
        catch (AttachmentTooLargeException)
        {
            await stream.DisposeAsync();
            return new(InboxAttachmentDownloadStatus.TooLarge);
        }
        catch (Exception exception) when (exception is IOException or FormatException)
        {
            await stream.DisposeAsync();
            return new(InboxAttachmentDownloadStatus.Unavailable);
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    /// <summary>Nonsecret identity stamp prevents old links reading a replacement account with coincident UIDs.</summary>
    public static string MailboxIdentity(Guid destinationMailboxId, string username, string folder, LocalImapOptions configuration)
    {
        var identity = JsonSerializer.Serialize(new { destinationMailboxId, username, folder,
            configuration.Host, configuration.Port, configuration.UseSsl });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    public static IReadOnlyList<InboxAttachment> DescribeAttachments(MimeMessage message) =>
        EnumerateAttachments(message.Body).Select((attachment, index) =>
            new InboxAttachment(index, AttachmentFileName(attachment, index), attachment.ContentType.MimeType)).ToArray();

    private static IEnumerable<MimeEntity> EnumerateAttachments(MimeEntity? entity)
    {
        if (entity is Multipart multipart)
        {
            foreach (var child in multipart)
                foreach (var attachment in EnumerateAttachments(child)) yield return attachment;
            yield break;
        }
        if (entity is not MimePart && entity is not MessagePart) yield break;
        var fileName = entity.ContentDisposition?.FileName ?? entity.ContentType.Name;
        // Explicit attachments remain downloadable even if they also have a Content-ID.
        // Inline resources are rendered by the reader, not offered as file attachments.
        if (entity.IsAttachment ||
            (!string.IsNullOrWhiteSpace(fileName) && !string.Equals(entity.ContentDisposition?.Disposition, "inline", StringComparison.OrdinalIgnoreCase)
                && !(entity.ContentType.MediaType.Equals("image", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(entity.ContentId))))
            yield return entity;
    }

    private static string AttachmentFileName(MimeEntity attachment, int index)
    {
        var original = attachment.ContentDisposition?.FileName ?? attachment.ContentType.Name ?? string.Empty;
        var basename = original.Replace('\\', '/').Split('/').Last();
        var clean = new string(basename.Where(character => !char.IsControl(character) &&
            character is not '"' and not ':' and not '<' and not '>' and not '|' and not '?' and not '*').ToArray()).Trim().Trim('.');
        if (string.IsNullOrWhiteSpace(clean)) clean = attachment is MessagePart ? $"message-{index + 1}.eml" : $"attachment-{index + 1}";
        if (attachment is MessagePart && !clean.EndsWith(".eml", StringComparison.OrdinalIgnoreCase)) clean += ".eml";
        return clean.Length <= 180 ? clean : clean[..160] + (attachment is MessagePart ? ".eml" : Path.GetExtension(clean)[..Math.Min(Path.GetExtension(clean).Length, 16)]);
    }

    private sealed class AttachmentTooLargeException : IOException;

    private sealed class BoundedAttachmentStream(int maximumBytes) : MemoryStream
    {
        private void CheckLength(int count)
        {
            if (Position + count > maximumBytes) throw new AttachmentTooLargeException();
        }
        public override void Write(byte[] buffer, int offset, int count) { CheckLength(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { CheckLength(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { CheckLength(1); base.WriteByte(value); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); CheckLength(count);
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); CheckLength(buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }
    }
}
