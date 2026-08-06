namespace MailWinnow.Tests.Deployment;

public sealed class ContainerContractTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Theory]
    [InlineData("src/MailWinnow.Web/Dockerfile", "MailWinnow.Web.dll")]
    [InlineData("src/MailWinnow.Worker/Dockerfile", "MailWinnow.Worker.dll")]
    [InlineData("tools/MailWinnow.DbMigrator/Dockerfile", "MailWinnow.DbMigrator.dll")]
    public void ContainerRunsPublishedHostAsNonRoot(string relativePath, string assembly)
    {
        var dockerfile = File.ReadAllText(Path.Combine(RepositoryRoot, relativePath));

        Assert.Contains("USER $APP_UID", dockerfile, StringComparison.Ordinal);
        Assert.Contains(assembly, dockerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("database update", dockerfile, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ComposePassesSharedAndEnvironmentScopedConfigurationToBothHosts()
    {
        var compose = File.ReadAllText(Path.Combine(RepositoryRoot, "deploy/docker-compose.home-helm.yml"));

        Assert.Contains("ConnectionStrings__MailWinnow: ${ConnectionStrings__MailWinnow:?", compose, StringComparison.Ordinal);
        Assert.Contains("DataProtection__KeysPath: /var/lib/mailwinnow/data-protection-keys", compose, StringComparison.Ordinal);
        Assert.Contains("LocalImap__Host: ${LocalImap__Host:-mailwinnow-imap.clintandtara.com}", compose, StringComparison.Ordinal);
        Assert.Contains("LocalImap__AllowInvalidCertificate: ${LocalImap__AllowInvalidCertificate:-false}", compose, StringComparison.Ordinal);
        Assert.Contains("web:", compose, StringComparison.Ordinal);
        Assert.Contains("worker:", compose, StringComparison.Ordinal);
        Assert.Contains("profiles: [\"migration\"]", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("smtp", compose, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MailWinnow.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }
}
