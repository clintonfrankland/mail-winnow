using System.Security.Claims;
using MailWinnow.Core.Rules;
using MailWinnow.Infrastructure.Mailboxes;
using MailWinnow.Infrastructure.Persistence;
using MailWinnow.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace MailWinnow.Infrastructure.Rules;

public sealed record ReviewNavigationCounts(int Messages, int Senders);

public interface INavigationCountService
{
    Task<int?> GetInboxCountAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default);
    Task<ReviewNavigationCounts> GetReviewCountsAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default);
}

/// <summary>Scalar SQL projections only; never runs mailbox evaluation or opens an IMAP connection.</summary>
public sealed class NavigationCountService(MailWinnowDbContext db, IOwnershipAuthorizer ownership) : INavigationCountService
{
    public Task<int?> GetInboxCountAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        var owner = ownership.RequireCurrentUserId(user);
        return db.DestinationMailboxes.AsNoTracking().Where(x => x.OwnerUserId == owner && x.Enabled)
            .Select(x => x.InboxMessageCount).SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<ReviewNavigationCounts> GetReviewCountsAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        var owner = ownership.RequireCurrentUserId(user);
        return await BuildReviewCountQuery(owner).SingleOrDefaultAsync(cancellationToken) ?? new(0, 0);
    }

    internal IQueryable<ReviewNavigationCounts> BuildReviewCountQuery(string owner)
    {
        var pending = ReviewProjectionQueries.WithoutQueuedMessages(db, owner,
            db.SourceMessageHeaders.AsNoTracking().Where(header => header.EvaluationOutcome == RuleOutcome.Pending &&
                db.SourceMailboxes.Any(source => source.Id == header.SourceMailboxId && source.OwnerUserId == owner)));
        // Group and aggregate on the server. Sender grouping retains the UI's case-insensitive raw-From semantics.
        var senders = db.Database.IsSqlServer()
            ? pending.Select(header => EF.Functions.Collate((header.From ?? "(unknown sender)").ToUpper(), "Latin1_General_100_BIN2"))
            : pending.Select(header => (header.From ?? "(unknown sender)").ToUpper());
        return senders.GroupBy(_ => 1).Select(group => new ReviewNavigationCounts(group.Count(), group.Distinct().Count()));
    }
}

internal static class ReviewProjectionQueries
{
    public static IQueryable<SourceMessageHeader> WithoutQueuedMessages(MailWinnowDbContext db, string owner,
        IQueryable<SourceMessageHeader> headers) => headers.Where(header => !db.ReviewDecisionWorkItems.Any(work =>
            work.OwnerUserId == owner &&
            (work.Status == ReviewDecisionWorkStatus.Pending || work.Status == ReviewDecisionWorkStatus.Processing || work.Status == ReviewDecisionWorkStatus.Retrying) &&
            work.MessageIdsJson.ToLower().Contains(header.Id.ToString().ToLower())));
}
