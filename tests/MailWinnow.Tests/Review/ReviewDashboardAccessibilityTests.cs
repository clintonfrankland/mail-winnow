namespace MailWinnow.Tests.Review;

public sealed class ReviewDashboardAccessibilityTests
{
    [Fact]
    public void WebHostRegistersInteractiveServerComponents()
    {
        var source = File.ReadAllText(FindPage(Path.Combine("..", "..", "Program.cs")));

        Assert.Contains("AddInteractiveServerComponents()", source);
        Assert.Contains("AddInteractiveServerRenderMode()", source);
    }

    [Fact]
    public void DashboardProvidesAccessibleFiltersActionsAndAnnouncements()
    {
        var source = File.ReadAllText(FindReviewPage());

        Assert.Contains("aria-label=\"Message filters\"", source);
        Assert.Contains("aria-label=\"Review view\"", source);
        Assert.Contains("role=\"alert\"", source);
        Assert.Contains("role=\"status\"", source);
        Assert.DoesNotContain("Approve this message", source);
        Assert.DoesNotContain("Approve these messages", source);
        Assert.Contains("Allow sender @item.Sender", source);
        Assert.Contains("Allow domain @domain", source);
        Assert.Contains("Block sender @item.Sender", source);
        Assert.Contains("Block domain @blockedDomain", source);
        Assert.Contains("@rendermode InteractiveServer", source);
        Assert.Contains("@onclick=\"() => BlockAsync(item.Sender, RuleMatchType.ExactSender)\"", source);
        Assert.Contains("@onclick='() => SwitchViewAsync(\"sender\")'", source);
        Assert.Contains("private async Task SwitchViewAsync(string view) { View = view; await ReloadAsync(); }", source);
        Assert.Contains("await ReloadAsync();", source);
        Assert.DoesNotContain("action=\"/review/messages/approve\"", source);
        Assert.Contains("href=\"/rules\">Manage rules", source);
        Assert.DoesNotContain("name=\"outcome\"", source);
        Assert.Contains("RuleOutcome.Pending", source);
        Assert.Contains("Messages disappear after a one-time decision or a matching sender/domain rule handles them.", source);
    }

    [Fact]
    public void RulesPageProvidesCreateEditRemoveAndRetentionControls()
    {
        var source = File.ReadAllText(FindPage("Rules.razor"));
        var navigation = File.ReadAllText(FindPage(Path.Combine("..", "Layout", "NavMenu.razor")));

        Assert.Contains("@page \"/rules\"", source);
        Assert.Contains("@attribute [Authorize]", source);
        Assert.Contains("Create rule", source);
        Assert.Contains("<option value=\"ExactSender\">Exact sender address</option>", source);
        Assert.Contains("<option value=\"SenderDomain\">Sender domain</option>", source);
        Assert.Contains("<option value=\"PermanentlyBlock\">Always block</option>", source);
        Assert.Contains("Edit rule", source);
        Assert.Contains("Save changes", source);
        Assert.Contains("Remove rule", source);
        Assert.Contains("returnUrl=/rules", source);
        Assert.Contains("<option value=\"\" selected=\"@(selected is null)\">Forever</option>", source);
        Assert.Contains("<option value=\"30\" selected=\"@(selected == 30)\">1 month</option>", source);
        Assert.Contains("<option value=\"7\" selected=\"@(selected == 7)\">1 week</option>", source);
        Assert.Contains("<option value=\"3\" selected=\"@(selected == 3)\">3 days</option>", source);
        Assert.Contains("<option value=\"1\" selected=\"@(selected == 1)\">1 day</option>", source);
        Assert.Contains("href=\"rules\"", navigation);
    }

    private static string FindReviewPage() => FindPage("Review.razor");

    private static string FindPage(string relativePath)
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            var candidate = Path.GetFullPath(Path.Combine(current.FullName, "src", "MailWinnow.Web", "Components", "Pages", relativePath));
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"{relativePath} was not found from the test output directory.");
    }
}
