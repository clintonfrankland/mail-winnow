namespace MailWinnow.Core.Abstractions;

/// <summary>
/// Processes the configured source mailboxes once. Implementations belong to Infrastructure.
/// </summary>
public interface IMailboxProcessor
{
    Task ProcessAsync(CancellationToken cancellationToken);
}
