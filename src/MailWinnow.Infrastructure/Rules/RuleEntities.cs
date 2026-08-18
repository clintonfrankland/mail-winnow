using MailWinnow.Core.Rules;

namespace MailWinnow.Infrastructure.Rules;

public sealed class MailRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string OwnerUserId { get; set; }
    public RuleAction Action { get; set; }
    public RuleScope Scope { get; set; }
    public RuleMatchType MatchType { get; set; }
    public required string MatchValue { get; set; }
    public Guid? SourceMailboxId { get; set; }
    public DateTimeOffset? EffectiveUtc { get; set; }
    public DateTimeOffset? ExpiresUtc { get; set; }
    /// <summary>Destination retention in days. Null keeps the moved message indefinitely.</summary>
    public int? DeliveredMessageRetentionDays { get; set; }
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

public enum ReviewDecisionWorkStatus { Pending, Processing, Retrying, Completed, Failed }

public sealed class ReviewDecisionWorkItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string OwnerUserId { get; set; }
    public RuleAction Action { get; set; }
    public RuleMatchType MatchType { get; set; }
    public required string MatchValue { get; set; }
    public int? RetentionDays { get; set; }
    public required string MessageIdsJson { get; set; }
    public required string IdempotencyKey { get; set; }
    public ReviewDecisionWorkStatus Status { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset NextAttemptUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }
    public string? LastError { get; set; }
}
