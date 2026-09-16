using System.Security.Claims;

namespace MailWinnow.Infrastructure.Outgoing;

public enum OutgoingState { Queued, Sending, Sent, Failed, OutcomeUnknown }
public enum SentCopyPolicy { ProviderSaves, LocalSentFolder }
public sealed record SendingAccountView(Guid Id, Guid? SourceMailboxId, string DisplayName, string FromAddress,
    string Host, int Port, bool UseStartTls, bool UseAuthentication, string Username, bool HasPassword,
    bool Enabled, SentCopyPolicy SentCopyPolicy, string SentFolder);
public sealed record SaveSendingAccountRequest(Guid? Id, Guid? SourceMailboxId, string DisplayName, string FromAddress,
    string Host, int Port, bool UseStartTls, bool UseAuthentication, string Username, string? Password,
    bool Enabled, SentCopyPolicy SentCopyPolicy, string SentFolder = "Sent");
public sealed record ReplySource(Guid DestinationMailboxId, string DestinationFolder, string MailboxIdentity, uint Uid, uint UidValidity);
public sealed record DraftAttachmentView(Guid Id, string FileName, string ContentType, long Length);
public sealed record MessageDraftView(Guid Id, long Revision, Guid? SendingAccountId, string To, string Cc, string Bcc,
    string Subject, string Body, IReadOnlyList<DraftAttachmentView> Attachments, DateTimeOffset UpdatedUtc, Guid? OutboxId);
public sealed record SaveDraftRequest(Guid Id, long Revision, Guid? SendingAccountId, string To, string Cc, string Bcc, string Subject, string Body);
public sealed record OutboxMessageView(Guid Id, Guid DraftId, string Subject, OutgoingState State, DateTimeOffset CreatedUtc,
    DateTimeOffset? SentUtc, string? FailureCode, string? SentCopyStatus, bool CanRetry);
public interface IOutgoingMailService
{
    Task<IReadOnlyList<SendingAccountView>> ListSendingAccountsAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default);
    Task<SendingAccountView> SaveSendingAccountAsync(ClaimsPrincipal user, SaveSendingAccountRequest request, CancellationToken cancellationToken = default);
    Task<MessageDraftView> CreateReplyAsync(ClaimsPrincipal user, ReplySource source, bool replyAll, CancellationToken cancellationToken = default);
    Task<MessageDraftView> CreateDraftAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MessageDraftView>> ListDraftsAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default);
    Task<MessageDraftView> GetDraftAsync(ClaimsPrincipal user, Guid id, CancellationToken cancellationToken = default);
    Task<MessageDraftView> SaveDraftAsync(ClaimsPrincipal user, SaveDraftRequest request, CancellationToken cancellationToken = default);
    Task<MessageDraftView> AddAttachmentAsync(ClaimsPrincipal user, Guid draftId, long revision, string fileName, string contentType, Stream content, CancellationToken cancellationToken = default);
    Task<MessageDraftView> RemoveAttachmentAsync(ClaimsPrincipal user, Guid draftId, long revision, Guid attachmentId, CancellationToken cancellationToken = default);
    Task DiscardDraftAsync(ClaimsPrincipal user, Guid id, long revision, CancellationToken cancellationToken = default);
    Task<OutboxMessageView> QueueAsync(ClaimsPrincipal user, Guid draftId, long revision, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OutboxMessageView>> ListOutboxAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default);
    Task<OutboxMessageView> RetryAsync(ClaimsPrincipal user, Guid outboxId, CancellationToken cancellationToken = default);
}
public sealed class OutgoingMailException(string message) : InvalidOperationException(message);
