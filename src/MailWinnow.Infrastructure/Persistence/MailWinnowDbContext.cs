using MailWinnow.Core.Rules;
using MailWinnow.Infrastructure.Security;
using MailWinnow.Infrastructure.Mailboxes;
using MailWinnow.Infrastructure.Rules;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace MailWinnow.Infrastructure.Persistence;

/// <summary>
/// EF Core context for MailWinnow's application data.
/// </summary>
public sealed class MailWinnowDbContext(DbContextOptions<MailWinnowDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<SourceMailbox> SourceMailboxes => Set<SourceMailbox>();
    public DbSet<DestinationMailbox> DestinationMailboxes => Set<DestinationMailbox>();
    public DbSet<SourceMailboxFolderSyncState> SourceMailboxFolderSyncStates => Set<SourceMailboxFolderSyncState>();
    public DbSet<SourceMessageHeader> SourceMessageHeaders => Set<SourceMessageHeader>();
    public DbSet<MailRule> MailRules => Set<MailRule>();
    public DbSet<MessageDecision> MessageDecisions => Set<MessageDecision>();
    public DbSet<MessageDelivery> MessageDeliveries => Set<MessageDelivery>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<WorkerHeartbeat> WorkerHeartbeats => Set<WorkerHeartbeat>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<SourceMailbox>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.OwnerUserId).HasMaxLength(450).IsRequired();
            entity.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Host).HasMaxLength(255).IsRequired();
            entity.Property(x => x.Username).HasMaxLength(320).IsRequired();
            entity.Property(x => x.ProtectedCredential).IsRequired();
            entity.Property(x => x.SelectedFoldersJson).IsRequired();
            entity.Property(x => x.PollingStatus).HasMaxLength(64);
            entity.Property(x => x.SanitizedError).HasMaxLength(512);
            entity.HasIndex(x => new { x.OwnerUserId, x.DisplayName });
        });
        builder.Entity<SourceMailboxFolderSyncState>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.FolderName).HasMaxLength(500).IsRequired();
            entity.HasIndex(x => new { x.SourceMailboxId, x.FolderName }).IsUnique();
        });
        builder.Entity<SourceMessageHeader>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.FolderName).HasMaxLength(500).IsRequired();
            entity.Property(x => x.MessageId).HasMaxLength(998);
            entity.Property(x => x.From).HasMaxLength(2000);
            entity.Property(x => x.To).HasMaxLength(2000);
            entity.Property(x => x.Subject).HasMaxLength(2000);
            entity.Property(x => x.EvaluationOutcome)
                .HasDefaultValue(RuleOutcome.Pending);
            entity.HasIndex(x => new { x.SourceMailboxId, x.FolderName, x.UidValidity, x.Uid }).IsUnique();
        });
        builder.Entity<MailRule>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.OwnerUserId).HasMaxLength(450).IsRequired();
            entity.Property(x => x.MatchValue).HasMaxLength(2000).IsRequired();
            entity.Property(x => x.DeliveredMessageRetentionDays)
                .HasDefaultValue(MailRule.DefaultDeliveredMessageRetentionDays);
            entity.HasIndex(x => new { x.OwnerUserId, x.Scope, x.SourceMailboxId });
        });
        builder.Entity<MessageDecision>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.OwnerUserId).HasMaxLength(450).IsRequired();
            entity.HasIndex(x => new { x.OwnerUserId, x.SourceMessageHeaderId }).IsUnique();
        });
        builder.Entity<DestinationMailbox>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.OwnerUserId).HasMaxLength(450).IsRequired();
            entity.Property(x => x.Username).HasMaxLength(320).IsRequired();
            entity.Property(x => x.Folder).HasMaxLength(500).IsRequired();
            entity.Property(x => x.ProtectedCredential).IsRequired();
            entity.HasIndex(x => x.OwnerUserId).IsUnique();
        });
        builder.Entity<MessageDelivery>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.OwnerUserId).HasMaxLength(450).IsRequired();
            entity.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.RetryRequestedByUserId).HasMaxLength(450);
            entity.Property(x => x.DestinationFolder).HasMaxLength(500);
            entity.Property(x => x.LastFailureStage).HasMaxLength(32);
            entity.Property(x => x.SanitizedError).HasMaxLength(512);
            entity.HasIndex(x => x.SourceMessageHeaderId).IsUnique();
            entity.HasIndex(x => x.ApprovalRuleId);
            entity.HasIndex(x => new { x.OwnerUserId, x.State });
            entity.HasIndex(x => new { x.State, x.ExpiresUtc });
        });
        builder.Entity<AuditEvent>(entity => { entity.HasKey(x => x.Id); entity.Property(x => x.EventType).HasMaxLength(100).IsRequired(); entity.Property(x => x.ActorUserId).HasMaxLength(450); entity.Property(x => x.SubjectUserId).HasMaxLength(450); entity.Property(x => x.ResourceType).HasMaxLength(64); entity.Property(x => x.ResourceId).HasMaxLength(64); entity.Property(x => x.Detail).HasMaxLength(512); entity.HasIndex(x => x.OccurredUtc); });
        builder.Entity<WorkerHeartbeat>(entity => { entity.HasKey(x => x.Id); entity.Property(x => x.Status).HasMaxLength(64).IsRequired(); });
    }
}
