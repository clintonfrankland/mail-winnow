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
        var actions = File.ReadAllText(FindPage("ReviewDecisionActions.razor"));

        Assert.Contains("aria-label=\"Message filters\"", source);
        Assert.Contains("aria-label=\"Review view\"", source);
        Assert.Contains("role=\"alert\"", source);
        Assert.Contains("role=\"status\"", source);
        Assert.DoesNotContain("Approve this message", source);
        Assert.DoesNotContain("Approve these messages", source);
        Assert.Contains("<ReviewDecisionActions Senders=\"@(new[] { item.Sender })\"", source);
        Assert.Contains("<ReviewDecisionActions Senders=\"@group.Senders\"", source);
        Assert.DoesNotContain("@foreach (var sender in group.Senders) { <ReviewDecisionActions", source);
        Assert.DoesNotContain("<strong>Source</strong>", source);
        Assert.DoesNotContain("<strong>Local delivery</strong>", source);
        Assert.DoesNotContain("<strong>Rule</strong>", source);
        Assert.DoesNotContain("<strong>Retention</strong>", source);
        Assert.DoesNotContain("<strong>Matching context</strong>", source);
        Assert.DoesNotContain("@item.Account", source);
        Assert.DoesNotContain("@item.Outcome", source);
        Assert.Contains("<strong>Subjects:</strong>", source);
        Assert.Contains("<strong>Senders:</strong>", source);
        Assert.Contains("review-meta review-count", source);
        Assert.Contains("group.Count == 1 ? \"message\" : \"messages\"", source);
        Assert.DoesNotContain("<span class=\"pill\">@group.Count", source);
        Assert.Contains("Newest @group.MostRecentUtc", source);
        Assert.Contains("_busy ? _busyLabel : \"Allow sender\"", actions);
        Assert.Contains(">Allow domain</button>", actions);
        Assert.Contains(">Block sender</button>", actions);
        Assert.Contains(">Block domain</button>", actions);
        Assert.Contains(">Delete</button>", actions);
        Assert.Contains("aria-label=\"More decisions\"", actions);
        Assert.Contains("aria-expanded=\"@_menuOpen\"", actions);
        Assert.Contains("aria-busy=\"@_busy\"", actions);
        Assert.Contains("disabled=\"@_busy\"", actions);
        Assert.Contains("\"Allowing…\"", actions);
        Assert.Contains("\"Blocking…\"", actions);
        Assert.Contains("\"Deleting…\"", actions);
        Assert.Contains("aria-label=\"Target sender\"", actions);
        Assert.Contains("role=\"menu\"", actions);
        Assert.Contains("_menuOpen = false;", actions);
        Assert.DoesNotContain("@Sender</button>", actions);
        Assert.DoesNotContain("@domain</button>", actions);
        Assert.Contains("@rendermode InteractiveServer", source);
        Assert.Contains("@onclick='() => SwitchViewAsync(\"sender\")'", source);
        Assert.Contains("private async Task SwitchViewAsync(string view) { View = view; await ReloadAsync(); }", source);
        Assert.Contains("await ReloadAsync();", source);
        Assert.DoesNotContain("action=\"/review/messages/approve\"", source);
        Assert.Contains("href=\"/rules\">Manage rules", source);
        Assert.DoesNotContain("name=\"outcome\"", source);
        Assert.Contains("RuleOutcome.Pending", source);
        Assert.Contains("Messages disappear after a matching sender or domain rule handles them.", source);
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

    [Fact]
    public void InboxProvidesThreePaneReaderAndBlocksImagesByDefault()
    {
        var source = File.ReadAllText(FindPage("Inbox.razor"));
        var navigation = File.ReadAllText(FindPage(Path.Combine("..", "Layout", "NavMenu.razor")));

        Assert.DoesNotContain("class=\"inbox-folders\"", source);
        Assert.Contains("class=\"inbox-list\"", source);
        Assert.Contains("class=\"inbox-reader\"", source);
        Assert.Contains("Remote images are blocked", source);
        Assert.Contains(">Show images</button>", source);
        Assert.Contains("referrerpolicy=\"no-referrer\"", source);
        Assert.Contains("href=\"inbox\"", navigation);
        Assert.Contains("<InboxCount />", navigation);
        var count = File.ReadAllText(FindPage(Path.Combine("..", "Layout", "InboxCount.razor")));
        Assert.Contains("InteractiveServerRenderMode(prerender: false)", count);
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
