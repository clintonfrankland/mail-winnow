namespace MailWinnow.Core.Rules;

/// <summary>The durable decisions supported by the first MailWinnow rule engine.</summary>
public enum RuleAction
{
    PermanentlyAllow,
    TemporarilyAllow,
    PermanentlyBlock,
    ApproveOneMessage,
    PendingReview
}
