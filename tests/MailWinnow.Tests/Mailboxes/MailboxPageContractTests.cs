namespace MailWinnow.Tests.Mailboxes;

public sealed class MailboxPageContractTests
{
    [Fact]
    public void PageShowsManySourcesAndOneNonSecretDestinationSummary()
    {
        var source = File.ReadAllText(FindPage());

        Assert.Contains("id=\"source-mailboxes-heading\">Source mailboxes", source);
        Assert.Contains(">Add source</summary>", source);
        Assert.Contains("@foreach (var source in _sources)", source);
        Assert.Contains("id=\"destination-mailbox-heading\">Destination mailbox", source);
        Assert.Contains("Credential stored", source);
        Assert.Contains("@LocalImap.Value.Host", source);
        Assert.Contains("@LocalImap.Value.Port", source);
        Assert.Contains("Password</dt><dd>••••••••</dd>", source);
        Assert.DoesNotContain("ProtectedCredential", source);
    }

    [Fact]
    public void AddAndEditFormsAreHiddenUntilRequested()
    {
        var source = File.ReadAllText(FindPage());

        Assert.Contains("<details class=\"mailbox-editor add-source-editor\">", source);
        Assert.Contains(">Edit destination</summary>", source);
        Assert.Contains("Leave blank to keep current", source);
    }

    private static string FindPage()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "src", "MailWinnow.Web", "Components", "Pages", "Mailboxes.razor");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Mailboxes.razor was not found from the test output directory.");
    }
}
