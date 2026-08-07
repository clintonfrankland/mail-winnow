using MailWinnow.Core.Rules;
using MailWinnow.Infrastructure.Persistence;
using MailWinnow.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace MailWinnow.Infrastructure.Mailboxes;

public interface IBlockedMessageDeletionService
{
    Task<IReadOnlyList<Guid>> GetDueHeaderIdsAsync(CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid headerId, CancellationToken cancellationToken = default);
}

/// <summary>Durably removes blocked messages by their cataloged source UID without ever fetching or appending a body.</summary>
public sealed class BlockedMessageDeletionService(
    MailWinnowDbContext db,
    ICredentialProtectionService credentials,
    IImapConnectionService imap,
    IAuditRecorder? audit = null) : IBlockedMessageDeletionService
{
    private static readonly TimeSpan StaleClaimAge = TimeSpan.FromMinutes(5);

    public async Task<IReadOnlyList<Guid>> GetDueHeaderIdsAsync(CancellationToken cancellationToken = default)
    {
        var staleBefore = DateTimeOffset.UtcNow.Subtract(StaleClaimAge);
        var candidates = await db.SourceMessageHeaders.AsNoTracking()
            .Where(x => x.EvaluationOutcome == RuleOutcome.Block && x.BlockedSourceDeletedUtc == null)
            .Select(x => new { x.Id, x.ReceivedUtc, x.BlockedSourceDeletionStartedUtc }).ToListAsync(cancellationToken);
        return candidates.Where(x => x.BlockedSourceDeletionStartedUtc is null || x.BlockedSourceDeletionStartedUtc < staleBefore)
            .OrderBy(x => x.ReceivedUtc).Take(1000).Select(x => x.Id).ToArray();
    }

    public async Task DeleteAsync(Guid headerId, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var staleBefore = now.Subtract(StaleClaimAge);
        var snapshot = await db.SourceMessageHeaders.AsNoTracking().SingleOrDefaultAsync(x => x.Id == headerId, cancellationToken);
        if (snapshot is null || snapshot.EvaluationOutcome != RuleOutcome.Block || snapshot.BlockedSourceDeletedUtc is not null ||
            snapshot.BlockedSourceDeletionStartedUtc is { } started && started >= staleBefore) return;
        var claimQuery = db.SourceMessageHeaders.Where(x => x.Id == headerId && x.EvaluationOutcome == RuleOutcome.Block && x.BlockedSourceDeletedUtc == null);
        claimQuery = snapshot.BlockedSourceDeletionStartedUtc is null
            ? claimQuery.Where(x => x.BlockedSourceDeletionStartedUtc == null)
            : claimQuery.Where(x => x.BlockedSourceDeletionStartedUtc == snapshot.BlockedSourceDeletionStartedUtc);
        var claimed = await claimQuery.ExecuteUpdateAsync(setters => setters.SetProperty(x => x.BlockedSourceDeletionStartedUtc, now), cancellationToken);
        if (claimed != 1) return;

        var header = await db.SourceMessageHeaders.SingleAsync(x => x.Id == headerId, cancellationToken);
        await db.Entry(header).ReloadAsync(cancellationToken); // ExecuteUpdate bypasses tracked values.
        var source = await db.SourceMailboxes.SingleAsync(x => x.Id == header.SourceMailboxId, cancellationToken);
        var connection = new ImapConnectionSettings(source.Host, source.Port, source.UseSsl, source.Username,
            credentials.Unprotect(source.ProtectedCredential, CredentialKind.SourceImapPassword));
        var deleted = await imap.DeleteAndExpungeAsync(connection, header.FolderName, [header.Uid], header.UidValidity, cancellationToken);
        if (!deleted.Succeeded)
        {
            header.BlockedSourceDeletionStartedUtc = null;
            header.BlockedSourceDeletionError = deleted.Error ?? "The blocked source message could not be deleted.";
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        header.BlockedSourceDeletedUtc = DateTimeOffset.UtcNow;
        header.BlockedSourceDeletionStartedUtc = null;
        header.BlockedSourceDeletionError = null;
        await db.SaveChangesAsync(cancellationToken);
        if (audit is not null) await audit.RecordAsync("blocked-source.deleted", null, source.OwnerUserId, "header", header.Id.ToString("N"), cancellationToken: cancellationToken);
    }
}
