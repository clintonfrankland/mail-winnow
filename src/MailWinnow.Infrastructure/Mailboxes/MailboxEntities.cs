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
