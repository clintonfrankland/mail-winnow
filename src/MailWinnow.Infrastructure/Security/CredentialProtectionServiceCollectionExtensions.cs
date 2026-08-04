using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MailWinnow.Infrastructure.Security;

public static class CredentialProtectionServiceCollectionExtensions
{
    public const string KeyRingPathConfigurationKey = "DataProtection:KeyRingPath";
    public const string ApplicationName = "MailWinnow";

    public static IServiceCollection AddMailWinnowCredentialProtection(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var configuredPath = configuration[KeyRingPathConfigurationKey];
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            throw new InvalidOperationException(
                $"The {KeyRingPathConfigurationKey} configuration value is required. " +
                "It must identify the persistent key-ring directory shared by Web and Worker.");
        }

        var keyRingDirectory = new DirectoryInfo(Path.GetFullPath(configuredPath));
        if (!keyRingDirectory.Exists)
        {
            throw new InvalidOperationException(
                $"The configured Data Protection key-ring directory '{keyRingDirectory.FullName}' does not exist. " +
                "Mount or create the persistent key storage before starting MailWinnow.");
        }

        services.AddDataProtection()
            .SetApplicationName(ApplicationName)
            .PersistKeysToFileSystem(keyRingDirectory);
        services.AddSingleton<ICredentialProtectionService, CredentialProtectionService>();

        return services;
    }
}
