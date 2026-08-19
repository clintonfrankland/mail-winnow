namespace MailWinnow.Web.Components.Layout;

public static class NavigationCountLoader
{
    public static void Start(Func<Task> loadInboxCountAsync, Func<Task> loadReviewCountsAsync)
    {
        _ = loadInboxCountAsync();
        _ = loadReviewCountsAsync();
    }
}
