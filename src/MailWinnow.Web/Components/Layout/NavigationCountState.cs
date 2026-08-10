namespace MailWinnow.Web.Components.Layout;

public sealed class NavigationCountState
{
    public event Action? Changed;

    public int InboxCount { get; private set; }
    public int ReviewMessageCount { get; private set; }
    public int ReviewSenderCount { get; private set; }

    public void SetInboxCount(int count)
    {
        InboxCount = Math.Max(0, count);
        Changed?.Invoke();
    }

    public void SetReviewCounts(int messageCount, int senderCount)
    {
        ReviewMessageCount = Math.Max(0, messageCount);
        ReviewSenderCount = Math.Max(0, senderCount);
        Changed?.Invoke();
    }
}
