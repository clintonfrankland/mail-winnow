using MailWinnow.Core.Rules;
using MailWinnow.Infrastructure.Mailboxes;
using MailWinnow.Infrastructure.Persistence;
using MailWinnow.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("MailWinnow.Tests")]

namespace MailWinnow.Infrastructure.Rules;

/// <summary>Refreshes remote inbox counts outside interactive circuits. Last good snapshots survive failures/restarts.</summary>
public sealed class NavigationProjectionRefreshService(MailWinnowDbContext db, IImapConnectionService imap,
    ICredentialProtectionService credentials, IOptions<LocalImapOptions> options)
{
    public async Task RefreshInboxAsync(Guid destinationId, CancellationToken cancellationToken)
    {
        var destination = await db.DestinationMailboxes.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == destinationId && x.Enabled, cancellationToken);
        if (destination is null) return;
        var configuration = options.Value;
        var connection = new ImapConnectionSettings(configuration.Host, configuration.Port, configuration.UseSsl,
            destination.Username, credentials.Unprotect(destination.ProtectedCredential, CredentialKind.DestinationImapPassword));
        var snapshot = await imap.GetFolderSnapshotAsync(connection, destination.Folder, cancellationToken);
        if (!snapshot.Succeeded || snapshot.Value is null) return;
        var observed = DateTimeOffset.UtcNow;
        // Do not persist a snapshot if the destination identity changed while IMAP was being read.
        await db.DestinationMailboxes.Where(x => x.Id == destination.Id && x.Enabled &&
                x.Username == destination.Username && x.Folder == destination.Folder && x.ProtectedCredential == destination.ProtectedCredential)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.InboxMessageCount, snapshot.Value.Uids.Count)
                .SetProperty(x => x.InboxCountObservedUtc, observed), cancellationToken);
    }
}

/// <summary>One process-wide refresh, not one mailbox scan per browser tab. Hosted only by MailWinnow.Worker.</summary>
public sealed class NavigationProjectionRefreshWorker(IServiceScopeFactory scopes, ILogger<NavigationProjectionRefreshWorker> logger)
    : BackgroundService
{
    private readonly Dictionary<Guid, string> _temporaryRuleStates = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RefreshAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning(exception, "Navigation projections could not be refreshed"); }
            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
        }
    }

    internal async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await RefreshTemporaryRulesAsync(cancellationToken);
        await using var selectionScope = scopes.CreateAsyncScope();
        var database = selectionScope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
        var destinationIds = await database.DestinationMailboxes.AsNoTracking().Where(x => x.Enabled)
            .Select(x => x.Id).ToListAsync(cancellationToken);
        foreach (var destinationId in destinationIds)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<NavigationProjectionRefreshService>()
                    .RefreshInboxAsync(destinationId, timeout.Token);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Inbox count refresh failed for destination {DestinationId}", destinationId);
            }
        }
    }

    internal async Task RefreshTemporaryRulesAsync(CancellationToken cancellationToken)
    {
        await using var selectionScope = scopes.CreateAsyncScope();
        var database = selectionScope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
        // Only temporal rule metadata crosses the database boundary; ordinary counts do not load any rules or headers.
        var rules = await database.MailRules.AsNoTracking().Where(x => x.Action == RuleAction.TemporarilyAllow)
            .Select(x => new { x.Id, x.OwnerUserId, x.EffectiveUtc, x.ExpiresUtc }).ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var states = rules.Select(rule => new
        {
            rule.Id, rule.OwnerUserId,
            State = $"{rule.EffectiveUtc:O}|{rule.ExpiresUtc:O}|{(!rule.EffectiveUtc.HasValue || rule.EffectiveUtc <= now) && (!rule.ExpiresUtc.HasValue || rule.ExpiresUtc > now)}"
        }).ToList();
        foreach (var owner in states.GroupBy(x => x.OwnerUserId))
        {
            if (owner.All(rule => _temporaryRuleStates.GetValueOrDefault(rule.Id) == rule.State)) continue;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IRuleEvaluationService>()
                    .ReevaluateOwnedHeadersAsync(owner.Key, cancellationToken);
                foreach (var rule in owner) _temporaryRuleStates[rule.Id] = rule.State;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Leave the boundary unacknowledged so the next worker pass retries it.
                logger.LogWarning(exception, "Temporary rule boundary refresh failed for owner {OwnerId}", owner.Key);
            }
        }
        var retained = states.Select(x => x.Id).ToHashSet();
        foreach (var removed in _temporaryRuleStates.Keys.Where(id => !retained.Contains(id)).ToArray()) _temporaryRuleStates.Remove(removed);
    }
}
