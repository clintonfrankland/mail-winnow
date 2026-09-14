namespace MailWinnow.Web.Components.Layout;

public sealed class NavigationCountState
{
    private readonly object _gate = new();
    public event Action? Changed;

    public long InboxRevision { get; private set; }
    public long ReviewRevision { get; private set; }

    public int InboxCount { get; private set; }
    public int ReviewMessageCount { get; private set; }
    public int ReviewSenderCount { get; private set; }

    public void SetInboxCount(int count)
    {
        lock (_gate)
        {
            InboxCount = Math.Max(0, count);
            InboxRevision++;
        }
        Changed?.Invoke();
    }

    public void SetReviewCounts(int messageCount, int senderCount)
    {
        lock (_gate)
        {
            ReviewMessageCount = Math.Max(0, messageCount);
            ReviewSenderCount = Math.Max(0, senderCount);
            ReviewRevision++;
        }
        Changed?.Invoke();
    }

    public void ApplyInboxRefresh(int count, long expectedRevision)
    {
        lock (_gate)
        {
            if (InboxRevision != expectedRevision) return;
            InboxCount = Math.Max(0, count);
            InboxRevision++;
        }
        Changed?.Invoke();
    }

    public void ApplyReviewRefresh(int messageCount, int senderCount, long expectedRevision)
    {
        lock (_gate)
        {
            if (ReviewRevision != expectedRevision) return;
            ReviewMessageCount = Math.Max(0, messageCount);
            ReviewSenderCount = Math.Max(0, senderCount);
            ReviewRevision++;
        }
        Changed?.Invoke();
    }
}
