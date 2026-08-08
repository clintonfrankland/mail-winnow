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
        var senderSource = File.ReadAllText(FindPage("ReviewBySender.razor"));
        var actions = File.ReadAllText(FindPage("ReviewDecisionActions.razor"));

        Assert.Contains("aria-label=\"Message filters\"", source);
        Assert.Contains("aria-label=\"Message filters\"", senderSource);
        Assert.DoesNotContain("aria-label=\"Review view\"", source);
        Assert.DoesNotContain("By subject", source);
        Assert.DoesNotContain("By subject", senderSource);
        Assert.Contains("@page \"/review\"", source);
        Assert.Contains("@page \"/review/sender\"", senderSource);
        Assert.Contains("Message Review - Recent", source);
        Assert.Contains("Message Review - Sender", senderSource);
        Assert.Contains("role=\"alert\"", source);
        Assert.Contains("role=\"status\"", source);
        Assert.DoesNotContain("Approve this message", source);
        Assert.DoesNotContain("Approve these messages", source);
        Assert.Contains("<ReviewDecisionActions Senders=\"@(new[] { item.Sender })\"", source);
        Assert.Contains("<ReviewDecisionActions Senders=\"@group.Senders\"", senderSource);
        Assert.DoesNotContain("@foreach (var sender in group.Senders) { <ReviewDecisionActions", senderSource);
        Assert.DoesNotContain("<strong>Source</strong>", source);
        Assert.DoesNotContain("<strong>Local delivery</strong>", source);
        Assert.DoesNotContain("<strong>Rule</strong>", source);
        Assert.DoesNotContain("<strong>Retention</strong>", source);
        Assert.DoesNotContain("<strong>Matching context</strong>", source);
        Assert.DoesNotContain("@item.Account", source);
        Assert.DoesNotContain("@item.Outcome", source);
        Assert.Contains("<strong>Subjects:</strong>", senderSource);
        Assert.DoesNotContain("<strong>Senders:</strong>", senderSource);
        Assert.Contains("review-meta review-count", senderSource);
        Assert.Contains("group.Count == 1 ? \"message\" : \"messages\"", senderSource);
        Assert.DoesNotContain("<span class=\"pill\">@group.Count", senderSource);
        Assert.Contains("Newest @group.MostRecentUtc", senderSource);
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
        Assert.Contains("@rendermode InteractiveServer", senderSource);
        Assert.DoesNotContain("SwitchViewAsync", source);
        Assert.DoesNotContain("SwitchViewAsync", senderSource);
        Assert.Contains("await ReloadAsync();", source);
        Assert.DoesNotContain("action=\"/review/messages/approve\"", source);
        Assert.Contains("href=\"/rules\">Manage rules", source);
        Assert.DoesNotContain("name=\"outcome\"", source);
        Assert.Contains("RuleOutcome.Pending", source);
        Assert.Contains("Messages disappear after a matching sender or domain rule handles them.", source);

        var navigation = File.ReadAllText(FindPage(Path.Combine("..", "Layout", "NavMenu.razor")));
        Assert.Contains("> Review - Recent</NavLink>", navigation);
        Assert.Contains("> Review - Sender</NavLink>", navigation);
        Assert.DoesNotContain("> Message review</NavLink>", navigation);
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
