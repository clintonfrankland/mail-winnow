namespace MailWinnow.Tests.Review;

public sealed class ReviewDashboardAccessibilityTests
{
    [Fact]
    public void DashboardProvidesAccessibleFiltersActionsAndAnnouncements()
    {
        var source = File.ReadAllText(FindReviewPage());

        Assert.Contains("aria-label=\"Message filters\"", source);
        Assert.Contains("aria-label=\"Review view\"", source);
        Assert.Contains("role=\"alert\"", source);
        Assert.Contains("role=\"status\"", source);
        Assert.Contains("<label for=\"retention-@rule.Id\">", source);
        Assert.Contains("<label for=\"domain-rule\">", source);
        Assert.Contains("Allow domain @domain", source);
        Assert.Contains("Replace this rule", source);
    }

    private static string FindReviewPage()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "src", "MailWinnow.Web", "Components", "Pages", "Review.razor");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Review.razor was not found from the test output directory.");
    }
}
