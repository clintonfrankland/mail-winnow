namespace MailWinnow.Tests.Deployment;

public sealed class VisualDesignContractTests
{
    [Fact]
    public void SharedShellUsesProductBrandAndResponsiveNavigation()
    {
        var layout = Read("src", "MailWinnow.Web", "Components", "Layout", "MainLayout.razor");
        var navigation = Read("src", "MailWinnow.Web", "Components", "Layout", "NavMenu.razor");
        var application = Read("src", "MailWinnow.Web", "Components", "App.razor");
        var styles = Read("src", "MailWinnow.Web", "wwwroot", "app.css");
        var accessibility = Read("src", "MailWinnow.Web", "wwwroot", "accessibility.css");
        var branding = Read("src", "MailWinnow.Web", "wwwroot", "brand.css");
        var productMark = FindRoot("src", "MailWinnow.Web", "wwwroot", "mail-winnow-mark-bordered.png");

        Assert.Contains("Private household mail", layout);
        Assert.Contains("Mail Winnow", navigation);
        Assert.Contains("Mail worth keeping", navigation);
        Assert.Contains("mail-winnow-mark-bordered.png", navigation);
        Assert.Contains("brand-mark-plain", navigation);
        Assert.Contains("rel=\"icon\"", application);
        Assert.Contains("type=\"image/png\"", application);
        Assert.Contains("mail-winnow-mark-bordered.png", application);
        Assert.DoesNotContain("mail-winnow-mark.jpg", application);
        Assert.True(new FileInfo(productMark).Length > 0);
        Assert.DoesNotContain("clip-path: polygon", branding);
        Assert.Contains("object-fit: contain", branding);
        Assert.DoesNotContain("object-fit: cover", branding);
        Assert.DoesNotContain(".brand-mark {\n    background: #fff", branding);
        Assert.Contains("background: transparent !important", branding);
        Assert.Contains("border-radius: 0 !important", branding);
        Assert.Contains("box-shadow: none !important", branding);
        Assert.DoesNotContain("MailWinnow.Web", navigation);
        Assert.Contains("@media(max-width:700px)", styles);
        Assert.Contains("prefers-reduced-motion", accessibility);
    }

    [Fact]
    public void ProductionPagesDoNotExposeTemplateOrDiagnosticCopy()
    {
        var pages = Directory.GetFiles(FindRoot("src", "MailWinnow.Web", "Components", "Pages"), "*.razor");
        var content = string.Join('\n', pages.Select(File.ReadAllText));

        Assert.DoesNotContain("Hello, world!", content);
        Assert.DoesNotContain("Development Mode", content);
        Assert.DoesNotContain("Weather forecast", content);
        Assert.Contains("Private mail, thoughtfully filtered", content);
        Assert.Contains("Something went wrong", content);
    }

    [Fact]
    public void ApplicationPageHeadingsOnlyShowThePageTitle()
    {
        string[] pages = ["Inbox", "Rules", "Mailboxes", "Operations", "Review", "ReviewBySender", "Household"];

        foreach (var page in pages)
        {
            var source = Read("src", "MailWinnow.Web", "Components", "Pages", $"{page}.razor");
            var headingStart = source.IndexOf("<header class=\"page-heading\">", StringComparison.Ordinal);
            var headingEnd = source.IndexOf("</header>", headingStart, StringComparison.Ordinal);
            var heading = source[headingStart..(headingEnd + "</header>".Length)];

            Assert.DoesNotContain("class=\"eyebrow\"", heading);
            Assert.DoesNotContain("<p>", heading);
        }

        var recentReview = Read("src", "MailWinnow.Web", "Components", "Pages", "Review.razor");
        Assert.DoesNotContain("Messages disappear after a matching sender or domain rule handles them.", recentReview);
    }

    private static string Read(params string[] parts) => File.ReadAllText(FindRoot(parts));

    private static string FindRoot(params string[] parts)
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine([current.FullName, .. parts]);
            if (File.Exists(candidate) || Directory.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException($"Repository path was not found: {Path.Combine(parts)}");
    }
}
