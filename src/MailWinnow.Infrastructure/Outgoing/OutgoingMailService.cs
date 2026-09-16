using System.Security.Claims;
using MailWinnow.Infrastructure.Mailboxes;
using MailWinnow.Infrastructure.Persistence;
using MailWinnow.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Utils;

namespace MailWinnow.Infrastructure.Outgoing;

/// <summary>Each operation gets a new scope; no circuit-scoped context, network sending, or message retention changes.</summary>
public sealed class OutgoingMailService(IServiceScopeFactory scopes, OutgoingPayloadProtection protection) : IOutgoingMailService
{
    private static string Owner(IServiceProvider services, ClaimsPrincipal user) => services.GetRequiredService<IOwnershipAuthorizer>().RequireCurrentUserId(user);
    private static SendingAccountView View(SendingAccount account) => new(account.Id, account.SourceMailboxId, account.DisplayName, account.FromAddress, account.Host,
        account.Port, account.UseStartTls, account.UseAuthentication, account.Username, !string.IsNullOrEmpty(account.ProtectedPassword), account.Enabled, account.SentCopyPolicy, account.SentFolder);
    public async Task<IReadOnlyList<SendingAccountView>> ListSendingAccountsAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        using var scope = scopes.CreateScope(); var owner = Owner(scope.ServiceProvider, user);
        var accounts = await scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>().SendingAccounts.AsNoTracking().Where(account => account.OwnerUserId == owner).OrderBy(account => account.DisplayName).ToListAsync(cancellationToken);
        return accounts.Select(View).ToArray();
    }
    public async Task<SendingAccountView> SaveSendingAccountAsync(ClaimsPrincipal user, SaveSendingAccountRequest request, CancellationToken cancellationToken = default)
    {
        using var scope = scopes.CreateScope(); var owner = Owner(scope.ServiceProvider, user); var database = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
        var sender = OutgoingMessagePolicies.ParseSender(request.FromAddress, request.DisplayName);
        if (request.Host.Length > 255 || Uri.CheckHostName(request.Host) == UriHostNameType.Unknown || request.Port is < 1 or > 65535 || request.Username.Length > 320 || request.Username.Any(char.IsControl) || request.SentFolder.Length is < 1 or > 500 || request.SentFolder.Any(char.IsControl) || !Enum.IsDefined(request.SentCopyPolicy))
            throw new OutgoingMailException("Check the SMTP server, port, username and Sent folder.");
        if (request.SourceMailboxId.HasValue && !await database.SourceMailboxes.AnyAsync(source => source.Id == request.SourceMailboxId && source.OwnerUserId == owner, cancellationToken))
            throw new OutgoingMailException("Source account is not available.");
        var account = request.Id.HasValue ? await database.SendingAccounts.SingleOrDefaultAsync(account => account.Id == request.Id && account.OwnerUserId == owner, cancellationToken)
            ?? throw new OutgoingMailException("Sending account is not available.") : new SendingAccount { OwnerUserId = owner, DisplayName = "", FromAddress = "", Host = "" };
        account.DisplayName = request.DisplayName; account.FromAddress = sender.Address; account.SourceMailboxId = request.SourceMailboxId;
        account.Host = request.Host; account.Port = request.Port; account.UseStartTls = request.UseStartTls; account.UseAuthentication = request.UseAuthentication;
        account.Username = request.Username; account.Enabled = request.Enabled; account.SentCopyPolicy = request.SentCopyPolicy; account.SentFolder = request.SentFolder;
        if (!string.IsNullOrEmpty(request.Password))
        {
            if (request.Password.Length > 4096) throw new OutgoingMailException("SMTP password is too long.");
            account.ProtectedPassword = scope.ServiceProvider.GetRequiredService<ICredentialProtectionService>().Protect(request.Password, CredentialKind.SmtpPassword);
        }
        if (account.Enabled && account.UseAuthentication && (string.IsNullOrWhiteSpace(account.Username) || string.IsNullOrWhiteSpace(account.ProtectedPassword)))
            throw new OutgoingMailException("Enter SMTP credentials before enabling sending.");
        if (!request.Id.HasValue) database.SendingAccounts.Add(account);
        await database.SaveChangesAsync(cancellationToken); return View(account);
    }
    public async Task<MessageDraftView> CreateDraftAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        using var scope = scopes.CreateScope(); var owner = Owner(scope.ServiceProvider, user);
        return await CreateAsync(scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>(), owner, null, new("", "", "", "", ""), cancellationToken);
    }
    public async Task<MessageDraftView> CreateReplyAsync(ClaimsPrincipal user, ReplySource source, bool replyAll, CancellationToken cancellationToken = default)
    {
        // Fetch a read-only copy using the exact current destination identity; never trust client-supplied reply headers.
        DestinationMailbox destination; LocalImapOptions options; string owner;
        IReadOnlyList<SendingAccount> accounts;
        using (var scope = scopes.CreateScope())
        {
            owner = Owner(scope.ServiceProvider, user); var database = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
            options = scope.ServiceProvider.GetRequiredService<IOptions<LocalImapOptions>>().Value;
            destination = await database.DestinationMailboxes.AsNoTracking().SingleOrDefaultAsync(mailbox => mailbox.Id == source.DestinationMailboxId && mailbox.OwnerUserId == owner && mailbox.Enabled, cancellationToken)
                ?? throw new OutgoingMailException("The original mailbox is no longer available.");
            if (source.Uid == 0 || source.UidValidity == 0 || !string.Equals(destination.Folder, source.DestinationFolder, StringComparison.Ordinal) ||
                !string.Equals(InboxAttachmentService.MailboxIdentity(destination.Id, destination.Username, destination.Folder, options), source.MailboxIdentity, StringComparison.Ordinal))
                throw new OutgoingMailException("The original mailbox changed. Refresh the inbox and try again.");
            accounts = await database.SendingAccounts.AsNoTracking().Where(account => account.OwnerUserId == owner).ToListAsync(cancellationToken);

        }
        using var fetchScope = scopes.CreateScope();
        var password = fetchScope.ServiceProvider.GetRequiredService<ICredentialProtectionService>().Unprotect(destination.ProtectedCredential, CredentialKind.DestinationImapPassword);
        var result = await fetchScope.ServiceProvider.GetRequiredService<IImapConnectionService>().FetchMessageAsync(new(options.Host, options.Port, options.UseSsl, destination.Username, password), destination.Folder, source.Uid, source.UidValidity, cancellationToken);
        if (!result.Succeeded || result.Value is null) throw new OutgoingMailException("The original message could not be read. Refresh the inbox and try again.");
        using var original = result.Value;
        var self = accounts.Select(account => account.FromAddress).Append(destination.Username);
        var recipients = OutgoingMessagePolicies.ReplyRecipients(original, replyAll, self);
        // Legacy deliveries do not bind server/account identity. Always require explicit From selection; never infer it from coincident UIDs.
        Guid? selected = null;
        var references = original.References.Where(value => value.Length <= 998 && !value.Any(char.IsControl)).TakeLast(40).ToList();
        var inReplyTo = original.MessageId is { Length: <= 998 } id && !id.Any(char.IsControl) ? id : null;
        if (inReplyTo is not null && !references.Contains(inReplyTo)) references.Add(inReplyTo);
        using var saveScope = scopes.CreateScope();
        return await CreateAsync(saveScope.ServiceProvider.GetRequiredService<MailWinnowDbContext>(), owner, selected,
            new(recipients.To, recipients.Cc, "", OutgoingMessagePolicies.ReplySubject(original.Subject ?? ""), OutgoingMessagePolicies.Quote(original), inReplyTo, references.ToArray()), cancellationToken);
    }
    private async Task<MessageDraftView> CreateAsync(MailWinnowDbContext database, string owner, Guid? accountId, DraftContent content, CancellationToken cancellationToken)
    {
        if (await database.MessageDrafts.CountAsync(draft => draft.OwnerUserId == owner && draft.OutboxId == null, cancellationToken) >= 100)
            throw new OutgoingMailException("You have 100 saved drafts. Discard an old draft before creating another.");
        var draft = new MessageDraft { OwnerUserId = owner, SendingAccountId = accountId, ProtectedContent = "", ProtectedSubject = "" };
        draft.ProtectedContent = protection.Protect(owner, "draft", draft.Id, content);
        draft.ProtectedSubject = protection.Protect(owner, "draft-subject", draft.Id, content.Subject); database.MessageDrafts.Add(draft);
        await database.SaveChangesAsync(cancellationToken); return DraftView(draft, []);
    }
    public async Task<IReadOnlyList<MessageDraftView>> ListDraftsAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        using var scope = scopes.CreateScope(); var owner = Owner(scope.ServiceProvider, user); var database = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
        var drafts = await database.MessageDrafts.AsNoTracking().Where(draft => draft.OwnerUserId == owner && draft.OutboxId == null).OrderByDescending(draft => draft.UpdatedUtc).Take(100)
            .Select(draft => new { draft.Id, draft.Revision, draft.SendingAccountId, draft.ProtectedSubject, draft.UpdatedUtc }).ToListAsync(cancellationToken);
        return drafts.Select(draft => new MessageDraftView(draft.Id, draft.Revision, draft.SendingAccountId, "", "", "",
            protection.Unprotect<string>(owner, "draft-subject", draft.Id, draft.ProtectedSubject), "", [], draft.UpdatedUtc, null)).ToArray();
    }
    public async Task<MessageDraftView> GetDraftAsync(ClaimsPrincipal user, Guid id, CancellationToken cancellationToken = default)
    {
        using var scope = scopes.CreateScope(); var owner = Owner(scope.ServiceProvider, user); var database = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
        return await DraftViewAsync(database, await GetDraftEntity(database, owner, id, cancellationToken), cancellationToken);
    }
    private static async Task<MessageDraft> GetDraftEntity(MailWinnowDbContext database, string owner, Guid id, CancellationToken cancellationToken) =>
        await database.MessageDrafts.SingleOrDefaultAsync(draft => draft.Id == id && draft.OwnerUserId == owner, cancellationToken) ?? throw new OutgoingMailException("Draft is no longer available.");
    private static void RequireEditable(MessageDraft draft, long revision)
    {
        if (draft.OutboxId.HasValue) throw new OutgoingMailException("This draft is already queued and cannot be edited.");
        if (draft.Revision != revision) throw new OutgoingMailException("This draft changed in another tab. Reopen it before editing.");
    }
    public async Task<MessageDraftView> SaveDraftAsync(ClaimsPrincipal user, SaveDraftRequest request, CancellationToken cancellationToken = default)
    {
        using var scope = scopes.CreateScope(); var owner = Owner(scope.ServiceProvider, user); var database = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
        var draft = await GetDraftEntity(database, owner, request.Id, cancellationToken); RequireEditable(draft, request.Revision);
        if (request.Subject.Length > 998 || request.Subject.Any(char.IsControl) || request.Body.Length > OutgoingMessagePolicies.MaxBodyCharacters || request.To.Length > 16000 || request.Cc.Length > 16000 || request.Bcc.Length > 16000)
            throw new OutgoingMailException("Draft text or addresses exceed the supported size.");
        if (request.SendingAccountId.HasValue && !await database.SendingAccounts.AnyAsync(account => account.Id == request.SendingAccountId && account.OwnerUserId == owner, cancellationToken))
            throw new OutgoingMailException("Sending account is not available.");
        var content = protection.Unprotect<DraftContent>(owner, "draft", draft.Id, draft.ProtectedContent);
        draft.ProtectedContent = protection.Protect(owner, "draft", draft.Id, content with { To = request.To, Cc = request.Cc, Bcc = request.Bcc, Subject = request.Subject, Body = request.Body });
        draft.ProtectedSubject = protection.Protect(owner, "draft-subject", draft.Id, request.Subject);
        draft.SendingAccountId = request.SendingAccountId; draft.Revision++; draft.UpdatedUtc = DateTimeOffset.UtcNow;
        await SaveChanges(database, cancellationToken); return await DraftViewAsync(database, draft, cancellationToken);
    }
    public async Task<MessageDraftView> AddAttachmentAsync(ClaimsPrincipal user, Guid draftId, long revision, string fileName, string contentType, Stream content, CancellationToken cancellationToken = default)
    {
        // Bound upload before opening a database scope. Bytes never enter an unprotected filesystem file.
        using var buffer = new MemoryStream(); var chunk = new byte[81920]; int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > OutgoingMessagePolicies.MaxAttachmentBytes) throw new OutgoingMailException("Each attachment must be at most 25 MiB.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        using var scope = scopes.CreateScope(); var owner = Owner(scope.ServiceProvider, user); var database = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
        await using var transaction = await database.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        var draft = await GetDraftEntity(database, owner, draftId, cancellationToken); RequireEditable(draft, revision);
        var ownerBytes = await (from ownerAttachment in database.DraftAttachments join editable in database.MessageDrafts on ownerAttachment.DraftId equals editable.Id
            where ownerAttachment.OwnerUserId == owner && editable.OwnerUserId == owner && editable.OutboxId == null select (long)ownerAttachment.Length).SumAsync(cancellationToken);
        if (ownerBytes + buffer.Length > 200L * 1024 * 1024) throw new OutgoingMailException("Your editable draft attachments use the 200 MiB storage allowance. Discard old drafts before adding more attachments.");
        var existing = await database.DraftAttachments.Where(attachment => attachment.OwnerUserId == owner && attachment.DraftId == draftId).Select(attachment => attachment.Length).ToListAsync(cancellationToken);
        if (existing.Count >= 20 || existing.Sum(length => (long)length) + buffer.Length > OutgoingMessagePolicies.MaxTotalAttachmentBytes)
            throw new OutgoingMailException("Use at most 20 attachments totaling 30 MiB.");
        var attachment = new DraftAttachment { DraftId = draft.Id, OwnerUserId = owner, FileName = OutgoingMessagePolicies.SafeFileName(fileName),
            ContentType = MimeKit.ContentType.TryParse(contentType, out var parsed) && contentType.Length <= 200 ? parsed.MimeType : "application/octet-stream", Length = checked((int)buffer.Length), ProtectedContent = [] };
        attachment.ProtectedContent = protection.ProtectBytes(owner, "attachment", attachment.Id, buffer.ToArray());
        database.DraftAttachments.Add(attachment); draft.Revision++; draft.UpdatedUtc = DateTimeOffset.UtcNow;
        await SaveChanges(database, cancellationToken); await transaction.CommitAsync(cancellationToken);
        return await DraftViewAsync(database, draft, cancellationToken);
    }
    public async Task<MessageDraftView> RemoveAttachmentAsync(ClaimsPrincipal user, Guid draftId, long revision, Guid attachmentId, CancellationToken cancellationToken = default)
    {
        using var scope = scopes.CreateScope(); var owner = Owner(scope.ServiceProvider, user); var database = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
        var draft = await GetDraftEntity(database, owner, draftId, cancellationToken); RequireEditable(draft, revision);
        var attachment = await database.DraftAttachments.SingleOrDefaultAsync(attachment => attachment.Id == attachmentId && attachment.DraftId == draftId && attachment.OwnerUserId == owner, cancellationToken)
            ?? throw new OutgoingMailException("Attachment is no longer available.");
        database.DraftAttachments.Remove(attachment); draft.Revision++; draft.UpdatedUtc = DateTimeOffset.UtcNow;
        await SaveChanges(database, cancellationToken); return await DraftViewAsync(database, draft, cancellationToken);
    }
    public async Task DiscardDraftAsync(ClaimsPrincipal user, Guid id, long revision, CancellationToken cancellationToken = default)
    {
        using var scope = scopes.CreateScope(); var owner = Owner(scope.ServiceProvider, user); var database = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
        var draft = await GetDraftEntity(database, owner, id, cancellationToken); RequireEditable(draft, revision); database.MessageDrafts.Remove(draft); await SaveChanges(database, cancellationToken);
    }
    private MessageDraftView DraftView(MessageDraft draft, IReadOnlyList<DraftAttachmentView> attachments)
    {
        var content = protection.Unprotect<DraftContent>(draft.OwnerUserId, "draft", draft.Id, draft.ProtectedContent);
        return new(draft.Id, draft.Revision, draft.SendingAccountId, content.To, content.Cc, content.Bcc, content.Subject, content.Body, attachments, draft.UpdatedUtc, draft.OutboxId);
    }
    private async Task<MessageDraftView> DraftViewAsync(MailWinnowDbContext database, MessageDraft draft, CancellationToken cancellationToken)
    {
        var attachments = await database.DraftAttachments.AsNoTracking().Where(attachment => attachment.DraftId == draft.Id && attachment.OwnerUserId == draft.OwnerUserId)
            .Select(attachment => new DraftAttachmentView(attachment.Id, attachment.FileName, attachment.ContentType, attachment.Length)).ToListAsync(cancellationToken);
        return DraftView(draft, attachments);
    }
    private static async Task SaveChanges(MailWinnowDbContext database, CancellationToken cancellationToken)
    {
        try { await database.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { throw new OutgoingMailException("This draft changed in another tab. Reopen it before continuing."); }
    }
    public async Task<OutboxMessageView> QueueAsync(ClaimsPrincipal user, Guid draftId, long revision, CancellationToken cancellationToken = default)
    {
        using var scope = scopes.CreateScope(); var owner = Owner(scope.ServiceProvider, user); var database = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
        var existing = await database.OutgoingMessages.AsNoTracking().SingleOrDefaultAsync(message => message.OwnerUserId == owner && message.DraftId == draftId && message.DraftRevision == revision, cancellationToken);
        if (existing is not null) return OutboxView(existing);
        var draft = await GetDraftEntity(database, owner, draftId, cancellationToken); RequireEditable(draft, revision);
        var account = await database.SendingAccounts.AsNoTracking().SingleOrDefaultAsync(account => account.Id == draft.SendingAccountId && account.OwnerUserId == owner && account.Enabled, cancellationToken)
            ?? throw new OutgoingMailException("Choose an enabled sending account before sending.");
        var content = protection.Unprotect<DraftContent>(owner, "draft", draft.Id, draft.ProtectedContent);
        using var mime = new MimeMessage { MessageId = MimeUtils.GenerateMessageId(), Date = DateTimeOffset.UtcNow, Subject = content.Subject };
        mime.From.Add(OutgoingMessagePolicies.ParseSender(account.FromAddress, account.DisplayName));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddRecipients(string value, InternetAddressList target)
        {
            foreach (var address in OutgoingMessagePolicies.ParseRecipients(value).Mailboxes)
                if (seen.Add(address.Address)) target.Add(address);
        }
        AddRecipients(content.To, mime.To); AddRecipients(content.Cc, mime.Cc); AddRecipients(content.Bcc, mime.Bcc);
        if (seen.Count is < 1 or > 100) throw new OutgoingMailException("Enter between 1 and 100 recipients.");
        if (content.InReplyTo is not null) mime.InReplyTo = content.InReplyTo;
        if (content.References is not null) foreach (var reference in content.References) mime.References.Add(reference);
        var body = new BodyBuilder { TextBody = content.Body };
        var attachments = await database.DraftAttachments.AsNoTracking().Where(attachment => attachment.OwnerUserId == owner && attachment.DraftId == draftId).ToListAsync(cancellationToken);
        if (attachments.Count > 20 || attachments.Sum(attachment => (long)attachment.Length) > OutgoingMessagePolicies.MaxTotalAttachmentBytes)
            throw new OutgoingMailException("Attachment limits exceeded.");
        foreach (var attachment in attachments)
            body.Attachments.Add(attachment.FileName, protection.UnprotectBytes(owner, "attachment", attachment.Id, attachment.ProtectedContent), MimeKit.ContentType.Parse(attachment.ContentType));
        mime.Body = body.ToMessageBody();
        using var bytes = new MemoryStream(); await mime.WriteToAsync(bytes, cancellationToken);
        if (bytes.Length > OutgoingMessagePolicies.MaxMimeBytes) throw new OutgoingMailException("The encoded message exceeds 45 MiB. Remove attachments and try again.");
        if (await database.OutgoingMessages.CountAsync(message => message.OwnerUserId == owner && (message.State == OutgoingState.Queued || message.State == OutgoingState.Sending), cancellationToken) >= 100)
            throw new OutgoingMailException("Your outbox has 100 pending messages. Wait for sending to finish.");
        var settings = await BuildSettingsAsync(scope.ServiceProvider, database, owner, account, cancellationToken);
        var outgoing = new OutgoingMessage { OwnerUserId = owner, DraftId = draftId, DraftRevision = revision, SendingAccountId = account.Id, ProtectedMime = [], ProtectedSettings = "", ProtectedSubject = "" };
        outgoing.ProtectedMime = protection.ProtectBytes(owner, "mime", outgoing.Id, bytes.ToArray());
        outgoing.ProtectedSettings = protection.Protect(owner, "settings", outgoing.Id, settings);
        outgoing.ProtectedSubject = protection.Protect(owner, "subject", outgoing.Id, content.Subject);
        database.OutgoingMessages.Add(outgoing); draft.OutboxId = outgoing.Id; draft.Revision++; draft.UpdatedUtc = DateTimeOffset.UtcNow;
        try { await database.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException)
        {
            // Concurrent identical Send: the loser may replay only the exact committed submission.
            using var replayScope = scopes.CreateScope(); var replayDb = replayScope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
            var replay = await replayDb.OutgoingMessages.AsNoTracking().SingleOrDefaultAsync(message => message.OwnerUserId == owner && message.DraftId == draftId && message.DraftRevision == revision, cancellationToken);
            if (replay is not null) return OutboxView(replay);
            throw new OutgoingMailException("The draft changed before it could be queued. Reopen it and try again.");
        }
        return OutboxView(outgoing);
    }
    private static async Task<OutgoingSettings> BuildSettingsAsync(IServiceProvider services, MailWinnowDbContext database, string owner, SendingAccount account, CancellationToken cancellationToken)
    {
        var password = account.UseAuthentication && account.ProtectedPassword is not null
            ? services.GetRequiredService<ICredentialProtectionService>().Unprotect(account.ProtectedPassword, CredentialKind.SmtpPassword) : "";
        var connection = new SendingConnection(account.Host, account.Port, account.UseStartTls, account.UseAuthentication, account.Username, password);
        DestinationMailbox? destination = null; string? identity = null;
        if (account.SentCopyPolicy == SentCopyPolicy.LocalSentFolder)
        {
            destination = await database.DestinationMailboxes.AsNoTracking().SingleOrDefaultAsync(mailbox => mailbox.OwnerUserId == owner && mailbox.Enabled, cancellationToken)
                ?? throw new OutgoingMailException("Configure your local mailbox before choosing a local Sent copy.");
            if (string.Equals(destination.Folder, account.SentFolder, StringComparison.OrdinalIgnoreCase) || string.Equals(account.SentFolder, "Trash", StringComparison.OrdinalIgnoreCase))
                throw new OutgoingMailException("Choose a separate Sent folder, not Inbox or Trash.");
            identity = InboxAttachmentService.MailboxIdentity(destination.Id, destination.Username, destination.Folder, services.GetRequiredService<IOptions<LocalImapOptions>>().Value);
        }
        return new(connection, account.FromAddress, account.DisplayName, account.SentCopyPolicy, destination?.Id, identity, account.SentFolder);
    }
    private OutboxMessageView OutboxView(OutgoingMessage message) => new(message.Id, message.DraftId,
        protection.Unprotect<string>(message.OwnerUserId, "subject", message.Id, message.ProtectedSubject), message.State, message.CreatedUtc, message.SentUtc, message.FailureCode,
        message.SentCopyStatus, message.State == OutgoingState.Failed || message.State == OutgoingState.Sent && message.SentCopyStatus == "Failed");
    public async Task<IReadOnlyList<OutboxMessageView>> ListOutboxAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        using var scope = scopes.CreateScope(); var owner = Owner(scope.ServiceProvider, user); var database = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
        var messages = await database.OutgoingMessages.AsNoTracking().Where(message => message.OwnerUserId == owner).OrderByDescending(message => message.CreatedUtc).Take(100)
            .Select(message => new { message.Id, message.DraftId, message.ProtectedSubject, message.State, message.CreatedUtc, message.SentUtc, message.FailureCode, message.SentCopyStatus }).ToListAsync(cancellationToken);
        return messages.Select(message => new OutboxMessageView(message.Id, message.DraftId,
            protection.Unprotect<string>(owner, "subject", message.Id, message.ProtectedSubject), message.State, message.CreatedUtc,
            message.SentUtc, message.FailureCode, message.SentCopyStatus, message.State == OutgoingState.Failed || message.State == OutgoingState.Sent && message.SentCopyStatus == "Failed")).ToArray();
    }
    public async Task<OutboxMessageView> RetryAsync(ClaimsPrincipal user, Guid outboxId, CancellationToken cancellationToken = default)
    {
        using var scope = scopes.CreateScope(); var owner = Owner(scope.ServiceProvider, user); var database = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
        var message = await database.OutgoingMessages.AsNoTracking().SingleOrDefaultAsync(message => message.Id == outboxId && message.OwnerUserId == owner, cancellationToken)
            ?? throw new OutgoingMailException("Outgoing message is not available.");
        if (message.State == OutgoingState.Sent && message.SentCopyStatus == "Failed")
        {
            await database.OutgoingMessages.Where(item => item.Id == outboxId && item.OwnerUserId == owner && item.State == OutgoingState.Sent && item.SentCopyStatus == "Failed")
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.SentCopyStatus, "Pending").SetProperty(item => item.NextAttemptUtc, (DateTimeOffset?)null), cancellationToken);
            message.SentCopyStatus = "Pending"; return OutboxView(message);
        }
        if (message.State != OutgoingState.Failed) throw new OutgoingMailException("Only a definitely failed send may be retried. Outcome unknown must be checked with your mail provider; it is never resent automatically.");
        var account = await database.SendingAccounts.AsNoTracking().SingleOrDefaultAsync(account => account.Id == message.SendingAccountId && account.OwnerUserId == owner && account.Enabled, cancellationToken)
            ?? throw new OutgoingMailException("Enable and check the original sending account before retrying.");
        var previous = protection.Unprotect<OutgoingSettings>(owner, "settings", message.Id, message.ProtectedSettings);
        if (!string.Equals(previous.FromAddress, account.FromAddress, StringComparison.OrdinalIgnoreCase)) throw new OutgoingMailException("The sending address changed. This immutable message cannot use a different From address.");
        var settings = await BuildSettingsAsync(scope.ServiceProvider, database, owner, account, cancellationToken);
        var protectedSettings = protection.Protect(owner, "settings", message.Id, settings);
        var changed = await database.OutgoingMessages.Where(item => item.Id == outboxId && item.OwnerUserId == owner && item.State == OutgoingState.Failed)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.State, OutgoingState.Queued).SetProperty(item => item.NextAttemptUtc, (DateTimeOffset?)null)
                .SetProperty(item => item.FailureCode, (string?)null).SetProperty(item => item.Retryable, false).SetProperty(item => item.AttemptCount, 0).SetProperty(item => item.ProtectedSettings, protectedSettings), cancellationToken);
        if (changed == 0) throw new OutgoingMailException("Outgoing status changed. Refresh the outbox.");
        message.State = OutgoingState.Queued; message.FailureCode = null; return OutboxView(message);
    }
}
