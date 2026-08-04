using MailWinnow.Core.Rules;

namespace MailWinnow.Infrastructure.Rules;

public sealed class MailRule
{
    public const int DefaultDeliveredMessageRetentionDays = 30;

    public Guid Id { get; set; } = Guid.NewGuid();
    public required string OwnerUserId { get; set; }
    public RuleAction Action { get; set; }
    public RuleScope Scope { get; set; }
    public RuleMatchType MatchType { get; set; }
    public required string MatchValue { get; set; }
    public Guid? SourceMailboxId { get; set; }
    public DateTimeOffset? EffectiveUtc { get; set; }
    public DateTimeOffset? ExpiresUtc { get; set; }
    /// <summary>Retention policy for delivered copies. It is deliberately not an expiry date.</summary>
    public int DeliveredMessageRetentionDays { get; set; } = DefaultDeliveredMessageRetentionDays;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class MessageDecision
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string OwnerUserId { get; set; }
    public Guid SourceMessageHeaderId { get; set; }
    public RuleAction Action { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}
