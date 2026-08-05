namespace MailWinnow.Infrastructure.Mailboxes;

using MailWinnow.Core.Rules;

public sealed class SourceMailbox
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string OwnerUserId { get; set; }
    public required string DisplayName { get; set; }
    public required string Host { get; set; }
    public int Port { get; set; }
    public bool UseSsl { get; set; }
    public required string Username { get; set; }
    public required string ProtectedCredential { get; set; }
    public bool Enabled { get; set; } = true;
    public string SelectedFoldersJson { get; set; } = "[]";
    public string? PollingStatus { get; set; }
    public DateTimeOffset? LastSuccessfulConnectionUtc { get; set; }
    public string? SanitizedError { get; set; }
    public DateTimeOffset? LastSyncAttemptUtc { get; set; }
    public DateTimeOffset? LastSyncSucceededUtc { get; set; }
    public int LastSyncHeaderCount { get; set; }
    public DateTimeOffset? SyncRequestedUtc { get; set; }
}

/// <summary>Per-folder checkpoint. A changed UIDVALIDITY deliberately starts a new identity namespace.</summary>
public sealed class SourceMailboxFolderSyncState
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SourceMailboxId { get; set; }
    public required string FolderName { get; set; }
    public uint UidValidity { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
}

/// <summary>Catalog entry containing RFC headers only; message bodies are never persisted by synchronization.</summary>
public sealed class SourceMessageHeader
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SourceMailboxId { get; set; }
    public required string FolderName { get; set; }
    public uint UidValidity { get; set; }
    public uint Uid { get; set; }
    public string? MessageId { get; set; }
    public DateTimeOffset? Date { get; set; }
    public string? From { get; set; }
    public string? To { get; set; }
    public string? Subject { get; set; }
    public DateTimeOffset ReceivedUtc { get; set; }
    public RuleOutcome EvaluationOutcome { get; set; } = RuleOutcome.Pending;
    public DateTimeOffset? EvaluatedUtc { get; set; }
}

public sealed class DestinationMailbox
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string OwnerUserId { get; set; }
    public required string Username { get; set; }
    public required string ProtectedCredential { get; set; }
    public required string Folder { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>Durable state for the one permitted copy of an approved source message. No MIME content is stored here.</summary>
public sealed class MessageDelivery
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SourceMessageHeaderId { get; set; }
    public required string OwnerUserId { get; set; }
    /// <summary>The reusable rule that authorized this copy, when applicable.</summary>
    public Guid? ApprovalRuleId { get; set; }
    public MessageDeliveryState State { get; set; } = MessageDeliveryState.Pending;
    public uint? DestinationUid { get; set; }
    public uint? DestinationUidValidity { get; set; }
    public uint? DestinationUidFloor { get; set; }
    /// <summary>Destination identity captured when the copy was appended, so retention cleanup cannot follow later mailbox edits.</summary>
    public Guid? DestinationMailboxId { get; set; }
    public string? DestinationFolder { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FetchStartedUtc { get; set; }
    public DateTimeOffset? DeliveryStartedUtc { get; set; }
    public DateTimeOffset? DestinationAppendStartedUtc { get; set; }
    public DateTimeOffset? DeliveredUtc { get; set; }
    public DateTimeOffset? ExpiresUtc { get; set; }
    public DateTimeOffset? DeletionStartedUtc { get; set; }
    public DateTimeOffset? DeletedUtc { get; set; }
    public DateTimeOffset? RetryRequestedUtc { get; set; }
    public string? RetryRequestedByUserId { get; set; }
    public string? LastFailureStage { get; set; }
    public string? SanitizedError { get; set; }
}

public enum MessageDeliveryState { Pending, Fetching, Delivering, Delivered, RetryPending, Failed, Expired, Deleted }
