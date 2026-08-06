using MailWinnow.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MailWinnow.Tests.Security;

public sealed class CredentialProtectionTests : IDisposable
{
    private readonly string testRoot = Path.Combine(
        Path.GetTempPath(),
        $"mailwinnow-protection-{Guid.NewGuid():N}");

    [Fact]
    public void ProtectsAtRestAndDecryptsAfterProviderRestart()
    {
        var keyRing = CreateDirectory("shared");
        const string plaintext = "source-app-password";

        var encrypted = CreateService(keyRing).Protect(
            plaintext,
            CredentialKind.SourceImapAppPassword);

        Assert.DoesNotContain(plaintext, encrypted, StringComparison.Ordinal);
        Assert.Equal(
            plaintext,
            CreateService(keyRing).Unprotect(encrypted, CredentialKind.SourceImapAppPassword));
    }

    [Fact]
    public void RejectsWrongPurposeMissingKeyAndCorruptPayloadWithoutExposingCredential()
    {
        var encrypted = CreateService(CreateDirectory("original")).Protect(
            "never-log-or-display-this",
            CredentialKind.SourceImapPassword);

        var wrongPurpose = Assert.Throws<CredentialProtectionException>(() =>
            CreateService(Path.Combine(testRoot, "original")).Unprotect(
                encrypted,
                CredentialKind.DestinationImapPassword));
        Assert.DoesNotContain("never-log-or-display-this", wrongPurpose.ToString(), StringComparison.Ordinal);

        var missingKey = Assert.Throws<CredentialProtectionException>(() =>
            CreateService(CreateDirectory("different-key-ring")).Unprotect(
                encrypted,
                CredentialKind.SourceImapPassword));
        Assert.Contains("key is unavailable", missingKey.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Throws<CredentialProtectionException>(() =>
            CreateService(Path.Combine(testRoot, "original")).Unprotect(
                encrypted + "corrupt",
                CredentialKind.SourceImapPassword));
    }

    [Fact]
    public void FailsClearlyWhenKeyRingConfigurationOrMountIsMissing()
    {
        var services = new ServiceCollection();
        var missingConfiguration = new ConfigurationBuilder().Build();

        var configurationError = Assert.Throws<InvalidOperationException>(() =>
            services.AddMailWinnowCredentialProtection(missingConfiguration));
        Assert.Contains("DataProtection:KeysPath", configurationError.Message, StringComparison.Ordinal);

        var absentPath = Path.Combine(testRoot, "absent");
        var absentMount = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [CredentialProtectionServiceCollectionExtensions.KeysPathConfigurationKey] = absentPath
            })
            .Build();

        var mountError = Assert.Throws<InvalidOperationException>(() =>
            services.AddMailWinnowCredentialProtection(absentMount));
        Assert.Contains("does not exist", mountError.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(testRoot))
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private string CreateDirectory(string name)
    {
        var path = Path.Combine(testRoot, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static ICredentialProtectionService CreateService(string keyRingPath)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [CredentialProtectionServiceCollectionExtensions.KeysPathConfigurationKey] = keyRingPath
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMailWinnowCredentialProtection(configuration);

        return services.BuildServiceProvider().GetRequiredService<ICredentialProtectionService>();
    }
}
