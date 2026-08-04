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
            entity.HasIndex(x => new { x.SourceMailboxId, x.FolderName, x.UidValidity, x.Uid }).IsUnique();
        });
        builder.Entity<MailRule>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.OwnerUserId).HasMaxLength(450).IsRequired();
            entity.Property(x => x.MatchValue).HasMaxLength(2000).IsRequired();
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
    }
}
