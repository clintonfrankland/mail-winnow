namespace MailWinnow.Infrastructure.Outgoing;

public sealed class SendingAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string OwnerUserId { get; set; }
    public Guid? SourceMailboxId { get; set; }
    public required string DisplayName { get; set; }
    public required string FromAddress { get; set; }
    public required string Host { get; set; }
    public int Port { get; set; } = 587;
    public bool UseStartTls { get; set; } = true;
    public bool UseAuthentication { get; set; } = true;
    public string Username { get; set; } = "";
    public string? ProtectedPassword { get; set; }
    public bool Enabled { get; set; }
    public SentCopyPolicy SentCopyPolicy { get; set; }
    public string SentFolder { get; set; } = "Sent";
}
public sealed class MessageDraft
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string OwnerUserId { get; set; }
    public long Revision { get; set; } = 1;
    public Guid? SendingAccountId { get; set; }
    public required string ProtectedContent { get; set; }
    public required string ProtectedSubject { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public Guid? OutboxId { get; set; }
}
public sealed class DraftAttachment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DraftId { get; set; }
    public required string OwnerUserId { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public int Length { get; set; }
    public required byte[] ProtectedContent { get; set; }
}
public sealed class OutgoingMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string OwnerUserId { get; set; }
    public Guid DraftId { get; set; }
    public long DraftRevision { get; set; }
    public Guid SendingAccountId { get; set; }
    public required byte[] ProtectedMime { get; set; }
    public required string ProtectedSettings { get; set; }
    public required string ProtectedSubject { get; set; }
    public OutgoingState State { get; set; } = OutgoingState.Queued;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SentUtc { get; set; }
    public DateTimeOffset? NextAttemptUtc { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset? LeaseExpiresUtc { get; set; }
    public int AttemptCount { get; set; }
    public string? FailureCode { get; set; }
    public bool Retryable { get; set; }
    public string? SentCopyStatus { get; set; }
}
internal sealed record DraftContent(string To, string Cc, string Bcc, string Subject, string Body,
    string? InReplyTo = null, string[]? References = null);
internal sealed record OutgoingSettings(SendingConnection Connection, string FromAddress, string DisplayName,
    SentCopyPolicy SentCopyPolicy, Guid? DestinationMailboxId, string? DestinationIdentity, string SentFolder);
