using MailWinnow.Infrastructure.Mailboxes;
using MailWinnow.Infrastructure.Persistence;
using MailWinnow.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace MailWinnow.Infrastructure.Outgoing;

/// <summary>Fenced durable SMTP state, hosted in Worker only. No claims survive a lost submission outcome as retryable work.</summary>
public sealed class OutgoingQueueProcessor(IServiceScopeFactory scopes, OutgoingPayloadProtection protection, IOutgoingTransport transport)
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(3);
    /// <summary>Deletes only successful outbound storage, never any inbound or IMAP message. Kept states require human resolution.</summary>
    public async Task<int> CleanupSentAsync(CancellationToken cancellationToken = default)
    {
        using var scope = scopes.CreateScope(); var database = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        var candidates = await database.OutgoingMessages.AsNoTracking().Where(item => item.State == OutgoingState.Sent && item.SentUtc < cutoff &&
            (item.SentCopyStatus == "Saved" || item.SentCopyStatus == "ProviderSaves"))
            .OrderBy(item => item.SentUtc).Select(item => new { item.Id, item.OwnerUserId, item.DraftId }).Take(20).ToListAsync(cancellationToken);
        if (candidates.Count == 0) return 0;
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var deleted = 0;
        foreach (var candidate in candidates)
        {
            var removed = await database.OutgoingMessages.Where(item => item.Id == candidate.Id && item.OwnerUserId == candidate.OwnerUserId && item.State == OutgoingState.Sent &&
                item.SentUtc < cutoff && (item.SentCopyStatus == "Saved" || item.SentCopyStatus == "ProviderSaves"))
                .ExecuteDeleteAsync(cancellationToken);
            if (removed == 0) continue;
            await database.MessageDrafts.Where(draft => draft.Id == candidate.DraftId && draft.OwnerUserId == candidate.OwnerUserId && draft.OutboxId == candidate.Id).ExecuteDeleteAsync(cancellationToken);
            deleted++;
        }
        await transaction.CommitAsync(cancellationToken); return deleted;
    }
    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken = default)
    {
        OutgoingMessage? message; var lease = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        using (var scope = scopes.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
            await database.OutgoingMessages.Where(item => item.State == OutgoingState.Sending && item.LeaseExpiresUtc < now)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.State, OutgoingState.OutcomeUnknown)
                    .SetProperty(item => item.FailureCode, "WorkerInterruptedCheckProvider").SetProperty(item => item.LeaseToken, (Guid?)null).SetProperty(item => item.LeaseExpiresUtc, (DateTimeOffset?)null), cancellationToken);
            await database.OutgoingMessages.Where(item => item.State == OutgoingState.Sent && item.SentCopyStatus == "Copying" && item.LeaseExpiresUtc < now)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.SentCopyStatus, "OutcomeUnknown").SetProperty(item => item.LeaseToken, (Guid?)null).SetProperty(item => item.LeaseExpiresUtc, (DateTimeOffset?)null), cancellationToken);
            message = await database.OutgoingMessages.AsNoTracking().Where(item => item.State == OutgoingState.Queued && (item.NextAttemptUtc == null || item.NextAttemptUtc <= now))
                .OrderBy(item => item.CreatedUtc).FirstOrDefaultAsync(cancellationToken);
            if (message is not null)
            {
                var claimed = await database.OutgoingMessages.Where(item => item.Id == message.Id && item.State == OutgoingState.Queued && (item.NextAttemptUtc == null || item.NextAttemptUtc <= now))
                    .ExecuteUpdateAsync(update => update.SetProperty(item => item.State, OutgoingState.Sending).SetProperty(item => item.LeaseToken, lease)
                        .SetProperty(item => item.LeaseExpiresUtc, now + LeaseDuration).SetProperty(item => item.AttemptCount, item => item.AttemptCount + 1), cancellationToken);
                if (claimed == 0) return true;
                message.AttemptCount++;
            }
        }
        if (message is null) return await CopyNextSentAsync(cancellationToken);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var renewal = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var renewTask = RenewLeaseAsync(message.Id, lease, operation, renewal.Token);
        SubmissionResult outcome; OutgoingSettings? settings = null;
        try
        {
            settings = protection.Unprotect<OutgoingSettings>(message.OwnerUserId, "settings", message.Id, message.ProtectedSettings);
            var valid = false;
            using (var scope = scopes.CreateScope())
            {
                var database = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
                var account = await database.SendingAccounts.AsNoTracking().SingleOrDefaultAsync(account => account.Id == message.SendingAccountId && account.OwnerUserId == message.OwnerUserId && account.Enabled, operation.Token);
                valid = account is not null && account.FromAddress == settings.FromAddress && account.Host == settings.Connection.Host && account.Port == settings.Connection.Port && account.UseStartTls == settings.Connection.UseStartTls && account.UseAuthentication == settings.Connection.UseAuthentication && account.Username == settings.Connection.Username;
            }
            if (!valid) outcome = new(SubmissionOutcome.Failed, "SendingAccountChangedOrDisabled");
            else
            {
                using var bytes = new MemoryStream(protection.UnprotectBytes(message.OwnerUserId, "mime", message.Id, message.ProtectedMime), false);
                using var mime = await MimeMessage.LoadAsync(bytes, operation.Token);
                outcome = await transport.SubmitAsync(settings.Connection, mime, operation.Token);
            }
        }
        catch (OperationCanceledException) { outcome = new(SubmissionOutcome.OutcomeUnknown, "SubmissionInterruptedCheckProvider"); }
        catch (System.Security.Cryptography.CryptographicException) { outcome = new(SubmissionOutcome.Failed, "ProtectedMessageUnavailable"); }
        catch (Exception) { outcome = new(SubmissionOutcome.OutcomeUnknown, "SubmissionInterruptedCheckProvider"); }
        finally { await renewal.CancelAsync(); try { await renewTask; } catch (OperationCanceledException) { } }
        // Commit SMTP outcome separately from IMAP. A crash before this write leaves Sending, which expires to unknown.
        using (var scope = scopes.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
            var accepted = outcome.Outcome == SubmissionOutcome.Accepted;
            var retry = outcome.Outcome == SubmissionOutcome.Failed && outcome.Retryable && message.AttemptCount < 3;
            var state = accepted ? OutgoingState.Sent : outcome.Outcome == SubmissionOutcome.OutcomeUnknown ? OutgoingState.OutcomeUnknown : retry ? OutgoingState.Queued : OutgoingState.Failed;
            var copyStatus = accepted ? settings!.SentCopyPolicy == SentCopyPolicy.ProviderSaves ? "ProviderSaves" : "Pending" : null;
            await database.OutgoingMessages.Where(item => item.Id == message.Id && item.State == OutgoingState.Sending && item.LeaseToken == lease)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.State, state).SetProperty(item => item.SentUtc, accepted ? (DateTimeOffset?)DateTimeOffset.UtcNow : null)
                    .SetProperty(item => item.SentCopyStatus, copyStatus).SetProperty(item => item.FailureCode, outcome.FailureCode).SetProperty(item => item.Retryable, outcome.Retryable)
                    .SetProperty(item => item.NextAttemptUtc, retry ? (DateTimeOffset?)DateTimeOffset.UtcNow.AddSeconds(30 * message.AttemptCount) : null)
                    .SetProperty(item => item.LeaseToken, (Guid?)null).SetProperty(item => item.LeaseExpiresUtc, (DateTimeOffset?)null), CancellationToken.None);
        }
        return true;
    }
    private async Task RenewLeaseAsync(Guid messageId, Guid lease, CancellationTokenSource operation, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                using var scope = scopes.CreateScope(); var database = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
                var now = DateTimeOffset.UtcNow;
                var changed = await database.OutgoingMessages.Where(item => item.Id == messageId && item.LeaseToken == lease && item.LeaseExpiresUtc > now && item.State == OutgoingState.Sending)
                    .ExecuteUpdateAsync(update => update.SetProperty(item => item.LeaseExpiresUtc, now + LeaseDuration), cancellationToken);
                if (changed == 0) { await operation.CancelAsync(); return; }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception) { await operation.CancelAsync(); }
    }
    private async Task<bool> CopyNextSentAsync(CancellationToken cancellationToken)
    {
        OutgoingMessage? message; var lease = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        using (var scope = scopes.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
            message = await database.OutgoingMessages.AsNoTracking().Where(item => item.State == OutgoingState.Sent && item.SentCopyStatus == "Pending").OrderBy(item => item.CreatedUtc).FirstOrDefaultAsync(cancellationToken);
            if (message is null) return false;
            var claimed = await database.OutgoingMessages.Where(item => item.Id == message.Id && item.State == OutgoingState.Sent && item.SentCopyStatus == "Pending")
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.SentCopyStatus, "Copying").SetProperty(item => item.LeaseToken, lease).SetProperty(item => item.LeaseExpiresUtc, now + LeaseDuration), cancellationToken);
            if (claimed == 0) return true;
        }
        var status = "Failed";
        var appendStarted = false;
        try
        {
            var settings = protection.Unprotect<OutgoingSettings>(message.OwnerUserId, "settings", message.Id, message.ProtectedSettings);
            DestinationMailbox? destination; LocalImapOptions options;
            using (var databaseScope = scopes.CreateScope())
            {
                var database = databaseScope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
                destination = await database.DestinationMailboxes.AsNoTracking().SingleOrDefaultAsync(mailbox => mailbox.OwnerUserId == message.OwnerUserId && mailbox.Id == settings.DestinationMailboxId && mailbox.Enabled, cancellationToken);
                options = databaseScope.ServiceProvider.GetRequiredService<IOptions<LocalImapOptions>>().Value;
            }
            if (destination is not null && InboxAttachmentService.MailboxIdentity(destination.Id, destination.Username, destination.Folder, options) == settings.DestinationIdentity)
            {
                using var networkScope = scopes.CreateScope();
                var password = networkScope.ServiceProvider.GetRequiredService<ICredentialProtectionService>().Unprotect(destination.ProtectedCredential, CredentialKind.DestinationImapPassword);
                using var bytes = new MemoryStream(protection.UnprotectBytes(message.OwnerUserId, "mime", message.Id, message.ProtectedMime), false);
                using var mime = await MimeMessage.LoadAsync(bytes, cancellationToken);
                appendStarted = true;
                var appended = await networkScope.ServiceProvider.GetRequiredService<IImapConnectionService>().AppendMessageAsync(new(options.Host, options.Port, options.UseSsl, destination.Username, password), settings.SentFolder, mime, cancellationToken);
                status = appended.Succeeded ? "Saved" : appended.FailureKind is ImapFailureKind.Authentication or ImapFailureKind.MissingFolder
                    ? "Failed" : "OutcomeUnknown";
            }
        }
        catch (Exception) { status = appendStarted ? "OutcomeUnknown" : "Failed"; }
        using (var scope = scopes.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
            await database.OutgoingMessages.Where(item => item.Id == message.Id && item.State == OutgoingState.Sent && item.LeaseToken == lease && item.SentCopyStatus == "Copying")
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.SentCopyStatus, status).SetProperty(item => item.LeaseToken, (Guid?)null).SetProperty(item => item.LeaseExpiresUtc, (DateTimeOffset?)null), CancellationToken.None);
        }
        return true;
    }
}

public sealed class OutgoingMailWorker(OutgoingQueueProcessor processor, ILogger<OutgoingMailWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextCleanup = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow >= nextCleanup)
                {
                    nextCleanup = DateTimeOffset.UtcNow.AddHours(1);
                    await processor.CleanupSentAsync(stoppingToken);
                }
                if (await processor.ProcessNextAsync(stoppingToken)) continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("Outgoing mail processing paused after a persistence failure; durable state will be checked on the next poll."); }
            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
