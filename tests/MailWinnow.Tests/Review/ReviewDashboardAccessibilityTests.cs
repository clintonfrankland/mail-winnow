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
    public void DashboardProvidesAccessibleActionsAndAnnouncementsWithoutFilters()
    {
        var source = File.ReadAllText(FindReviewPage());
        var senderSource = File.ReadAllText(FindPage("ReviewBySender.razor"));
        var actions = File.ReadAllText(FindPage("ReviewDecisionActions.razor"));

        Assert.DoesNotContain("aria-label=\"Message filters\"", source);
        Assert.DoesNotContain("aria-label=\"Message filters\"", senderSource);
        Assert.DoesNotContain("SupplyParameterFromQuery", source);
        Assert.DoesNotContain("SupplyParameterFromQuery", senderSource);
        Assert.DoesNotContain("MailboxService", source);
        Assert.DoesNotContain("MailboxService", senderSource);
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
        Assert.Contains("class=\"decision-target-sender\"", actions);
        Assert.Contains("class=\"decision-retention\"", actions);
        Assert.Contains("private int? _retentionDays = 1;", actions);
        Assert.Contains("role=\"menu\"", actions);
        Assert.Contains("_menuOpen = false;", actions);
        Assert.DoesNotContain("@Sender</button>", actions);
        Assert.DoesNotContain("@domain</button>", actions);
        Assert.Contains("@rendermode InteractiveServer", source);
        Assert.Contains("@rendermode InteractiveServer", senderSource);
        Assert.DoesNotContain("SwitchViewAsync", source);
        Assert.DoesNotContain("SwitchViewAsync", senderSource);
        Assert.Contains("await ReloadAsync();", source);
        Assert.Contains("@inject IReviewDecisionQueue DecisionQueue", source);
        Assert.Contains("@inject IReviewDecisionQueue DecisionQueue", senderSource);
        Assert.Contains("StateHasChanged();", source);
        Assert.Contains("StateHasChanged();", senderSource);
        Assert.Contains("ReviewDecisionPageHelper.Matches", source);
        Assert.Contains("ReviewDecisionPageHelper.Matches", senderSource);
        Assert.DoesNotContain("action=\"/review/messages/approve\"", source);
        Assert.Contains("href=\"/rules\">Manage rules", source);
        Assert.DoesNotContain("name=\"outcome\"", source);
        Assert.Contains("RuleOutcome.Pending", source);
        Assert.DoesNotContain("Messages disappear after a matching sender or domain rule handles them.", source);

        var mobileStyles = File.ReadAllText(FindPage(Path.Combine("..", "..", "wwwroot", "mobile.css")));
        Assert.Contains(".decision-rule-row>.decision-retention{flex:0 0 110px;height:48px;min-height:48px;max-width:110px", mobileStyles);
        Assert.Contains(".split-action-primary,.split-action-toggle{height:48px;min-height:48px}", mobileStyles);
        Assert.Contains(".split-action-toggle{flex:0 0 48px;min-width:48px", mobileStyles);

        var navigation = File.ReadAllText(FindPage(Path.Combine("..", "Layout", "NavMenu.razor")));
        Assert.Contains("@CountState.ReviewMessageCount pending messages", navigation);
        Assert.DoesNotContain("> Message review</NavLink>", navigation);
    }

    [Fact]
    public void NavigationIsMailFocusedOrderedAndShowsReviewCounts()
    {
        var navigation = File.ReadAllText(FindPage(Path.Combine("..", "Layout", "NavMenu.razor")));
        var navigationStyles = File.ReadAllText(FindPage(Path.Combine("..", "Layout", "NavMenu.razor.css")));

        Assert.DoesNotContain("Workspace", navigation);
        Assert.DoesNotContain("Overview", navigation);
        Assert.DoesNotContain("Administration", navigation);
        Assert.DoesNotContain("admin/operations", navigation);
        Assert.DoesNotContain("admin/household", navigation);
        Assert.DoesNotContain("<InboxCount />", navigation);
        Assert.DoesNotContain("Inboxes", navigation);
        Assert.True(navigation.IndexOf("> Inbox ", StringComparison.Ordinal) < navigation.IndexOf("Review - Recent", StringComparison.Ordinal));
        Assert.True(navigation.IndexOf("Review - Recent", StringComparison.Ordinal) < navigation.IndexOf("Review - Sender", StringComparison.Ordinal));
        Assert.True(navigation.IndexOf("Review - Sender", StringComparison.Ordinal) < navigation.IndexOf("> Rules</NavLink>", StringComparison.Ordinal));
        Assert.True(navigation.IndexOf("> Rules</NavLink>", StringComparison.Ordinal) < navigation.IndexOf("> Mailboxes</NavLink>", StringComparison.Ordinal));
        Assert.True(navigation.IndexOf("> Mailboxes</NavLink>", StringComparison.Ordinal) < navigation.IndexOf("> Sign out</button>", StringComparison.Ordinal));
        Assert.Contains("@CountState.InboxCount inbox messages", navigation);
        Assert.Contains("@CountState.ReviewMessageCount pending messages", navigation);
        Assert.Contains("@CountState.ReviewSenderCount pending senders", navigation);
        Assert.Contains("InteractiveServerRenderMode(prerender: false)", navigation);
        Assert.Contains("ScopeFactory.CreateAsyncScope()", navigation);
        Assert.Contains("GetRequiredService<IInboxReaderService>()", navigation);
        Assert.Contains("GetRequiredService<IMessageReviewService>()", navigation);
        Assert.Contains("CountState.Changed += OnCountsChanged", navigation);
        Assert.Contains("CountState.Changed -= OnCountsChanged", navigation);
        Assert.Contains(".sidebar-count", navigationStyles);
        Assert.Contains("margin-top:auto", navigationStyles);
        Assert.Contains("class=\"app-version\"", navigation);
        Assert.Contains("typeof(Program).Assembly.GetName().Version", navigation);
        Assert.DoesNotContain("ToString(3)", navigation);
        Assert.Contains(">v@(AppVersion)</div>", navigation);
        Assert.DoesNotContain(">v@AppVersion</div>", navigation);
        Assert.True(navigation.IndexOf("class=\"app-version\"", StringComparison.Ordinal) < navigation.IndexOf("> Sign out</button>", StringComparison.Ordinal));
        Assert.Contains(".app-version", navigationStyles);
    }

    [Fact]
    public void NavigationRendersBeforeCountLoadingStarts()
    {
        var navigation = File.ReadAllText(FindPage(Path.Combine("..", "Layout", "NavMenu.razor")));

        Assert.Contains("> Inbox ", navigation);
        Assert.Contains("Review - Recent", navigation);
        Assert.Contains("Review - Sender", navigation);
        Assert.Contains("> Rules</NavLink>", navigation);
        Assert.Contains("> Mailboxes</NavLink>", navigation);
        Assert.Contains("> Sign out</button>", navigation);
        Assert.Contains("OnAfterRenderAsync(bool firstRender)", navigation);
        Assert.Contains("await NavigationCountLoader.RunAsync(", navigation);
        Assert.Contains("CountRefreshInterval", navigation);
        Assert.Contains("_refreshCancellation.Token", navigation);
        Assert.DoesNotContain("override async Task OnInitializedAsync()", navigation);
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
        Assert.Contains("Preview impact", source);
        Assert.Contains("Preview changes", source);
        Assert.Contains("Confirm and save rule", source);
        Assert.Contains("Rule impact preview", source);
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
        Assert.DoesNotContain("Remote images are blocked", source);
        Assert.Contains("class=\"reader-actions\"", source);
        Assert.Equal(2, source.Split("class=\"button-icon\"", StringSplitOptions.None).Length - 1);
        Assert.Equal(2, source.Split("aria-hidden=\"true\"", StringSplitOptions.None).Length - 1);
        Assert.Contains("<span>Show images</span>", source);
        Assert.Contains("@onclick=\"ShowImages\"", source);
        Assert.Contains("<iframe class=\"message-body\"", source);
        Assert.Contains("@key=\"ViewerKey\"", source);
        Assert.Contains("sandbox=\"allow-popups allow-popups-to-escape-sandbox\"", source);
        Assert.Contains("referrerpolicy=\"no-referrer\"", source);
        Assert.Contains("srcdoc=", source);
        Assert.DoesNotContain("<pre class=\"message-body\"", source);
        Assert.Contains("<span>Delete</span>", source);
        Assert.True(source.IndexOf("<span>Show images</span>", StringComparison.Ordinal) < source.IndexOf("<span>Delete</span>", StringComparison.Ordinal));
        Assert.Contains("_messages = _messages.Where", source);
        Assert.Contains("await DeletionQueue.QueueAsync", source);
        Assert.Contains("await SelectAsync(_messages[Math.Min", source);
        Assert.Contains("referrerpolicy=\"no-referrer\"", source);
        Assert.Contains("href=\"inbox\"", navigation);
        Assert.Contains("@CountState.InboxCount inbox messages", navigation);
        Assert.Contains("InteractiveServerRenderMode(prerender: false)", navigation);
        Assert.Contains("ScopeFactory.CreateAsyncScope()", navigation);
        Assert.Contains("CountState.SetInboxCount(_messages.Count)", source);
    }

    [Fact]
    public void InboxRowsResetGlobalButtonLayoutAndPagesHavePhoneBreakpoints()
    {
        var inboxStyles = File.ReadAllText(FindPage("Inbox.razor.css"));
        var app = File.ReadAllText(FindPage(Path.Combine("..", "App.razor")));
        var mobileStyles = File.ReadAllText(FindPage(Path.Combine("..", "..", "wwwroot", "mobile.css")));
        var layoutStyles = File.ReadAllText(FindPage(Path.Combine("..", "Layout", "MainLayout.razor.css")));
        var navigationStyles = File.ReadAllText(FindPage(Path.Combine("..", "Layout", "NavMenu.razor.css")));
        var mailboxStyles = File.ReadAllText(FindPage("Mailboxes.razor.css"));

        Assert.Contains(".message-row{appearance:none", inboxStyles);
        Assert.Contains("align-items:stretch", inboxStyles);
        Assert.Contains("justify-content:flex-start", inboxStyles);
        Assert.Contains("overflow-x:hidden", inboxStyles);
        Assert.Contains("height:calc(100dvh - 161px)", inboxStyles);
        Assert.Contains("grid-template-rows:auto auto minmax(0,1fr)", inboxStyles);
        Assert.Contains("height:100%;min-height:0", inboxStyles);
        Assert.Contains("class=\"message-row-content\"", File.ReadAllText(FindPage("Inbox.razor")));
        Assert.Contains(".message-row-content strong,.message-row-content span,.message-row-content time", inboxStyles);
        Assert.Contains("class=\"message-row-topline\"", File.ReadAllText(FindPage("Inbox.razor")));
        Assert.Contains("class=\"message-sender\"", File.ReadAllText(FindPage("Inbox.razor")));
        Assert.Contains("class=\"retention-badge\" aria-label=\"Retention:", File.ReadAllText(FindPage("Inbox.razor")));
        Assert.Contains(".message-row-topline{align-items:center;display:flex!important", inboxStyles);
        Assert.Contains(".message-sender{flex:1 1 auto;min-width:0;overflow:hidden;text-overflow:ellipsis", inboxStyles);
        Assert.Contains(".retention-badge{background:var(--primary-soft);border-radius:999px;color:var(--primary);flex:0 0 auto", inboxStyles);
        Assert.Contains("@media(max-width:520px)", inboxStyles);
        Assert.Contains("mobile.css", app);
        Assert.Contains("@media(max-width:700px)", mobileStyles);
        Assert.Contains("overflow-x:hidden", mobileStyles);
        Assert.Contains("font-size:16px", mobileStyles);
        Assert.Contains("@media(max-width:760px)", layoutStyles);
        Assert.Contains("@media(max-width:760px)", navigationStyles);
        Assert.Contains("100dvh", navigationStyles);
        Assert.Contains("@media (max-width: 520px)", mailboxStyles);
    }

    [Fact]
    public void ReviewPreviewsAreOnDemandAccessibleAndKeepSenderRowStylesInParentScope()
    {
        var recent = File.ReadAllText(FindReviewPage());
        var sender = File.ReadAllText(FindPage("ReviewBySender.razor"));
        var preview = File.ReadAllText(FindPage("ReviewMessagePreview.razor"));
        var senderStyles = File.ReadAllText(FindPage("ReviewBySender.razor.css"));
        var previewStyles = File.ReadAllText(FindPage("ReviewMessagePreview.razor.css"));

        Assert.Contains("aria-expanded=\"@(expanded ? \"true\" : \"false\")\"", recent);
        Assert.Contains("aria-expanded=\"@(groupExpanded ? \"true\" : \"false\")\"", sender);
        Assert.Contains("aria-expanded=\"@(previewExpanded ? \"true\" : \"false\")\"", sender);
        Assert.Contains("@if (expanded) { <div id=\"preview-@item.Id\"><ReviewMessagePreview", recent);
        Assert.Contains("@if (previewExpanded) { <div id=\"preview-@item.Id\"><ReviewMessagePreview", sender);
        Assert.Contains("OrderByDescending(x => x.ReceivedUtc)", sender);
        Assert.Contains("Loading message preview", preview);
        Assert.Contains("sandbox=\"allow-popups\"", preview);
        Assert.Contains("referrerpolicy=\"no-referrer\"", preview);
        Assert.DoesNotContain("Show images", recent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Show images", sender, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Show images", preview, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".sender-message-list", senderStyles);
        Assert.Contains(".sender-message-row", senderStyles);
        Assert.Contains("@media(max-width:520px)", senderStyles);
        Assert.DoesNotContain(".sender-message-row", previewStyles);
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
