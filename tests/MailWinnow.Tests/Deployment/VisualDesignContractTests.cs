namespace MailWinnow.Tests.Deployment;

public sealed class VisualDesignContractTests
{
    [Fact]
    public void SharedShellUsesProductBrandAndResponsiveNavigation()
    {
        var layout = Read("src", "MailWinnow.Web", "Components", "Layout", "MainLayout.razor");
        var navigation = Read("src", "MailWinnow.Web", "Components", "Layout", "NavMenu.razor");
        var styles = Read("src", "MailWinnow.Web", "wwwroot", "app.css");
        var accessibility = Read("src", "MailWinnow.Web", "wwwroot", "accessibility.css");

        Assert.Contains("Private household mail", layout);
        Assert.Contains("Mail Winnow", navigation);
        Assert.Contains("Selective delivery", navigation);
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
