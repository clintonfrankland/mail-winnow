using System.ComponentModel.DataAnnotations;

namespace MailWinnow.Infrastructure.Mailboxes;

public sealed class LocalImapOptions
{
    public const string SectionName = "LocalImap";

    [Required] public string Host { get; init; } = string.Empty;
    [Range(1, 65535)] public int Port { get; init; }
    public bool UseSsl { get; init; }
    public bool AllowInvalidCertificate { get; init; }
}
