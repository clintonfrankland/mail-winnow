namespace MailWinnow.Infrastructure.Mailboxes;

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
