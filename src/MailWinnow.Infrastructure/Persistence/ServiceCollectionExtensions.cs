using MailWinnow.Infrastructure.Security;
using MailWinnow.Infrastructure.Mailboxes;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MailWinnow.Infrastructure.Persistence;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the SQL Server application context. The standard configuration provider maps
    /// <c>ConnectionStrings__MailWinnow</c> to <c>ConnectionStrings:MailWinnow</c>.
    /// </summary>
    public static IServiceCollection AddMailWinnowSqlServer(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDbContext<MailWinnowDbContext>(options =>
            options.UseSqlServer(configuration.GetConnectionString("MailWinnow")
                ?? throw new InvalidOperationException(
                    "The ConnectionStrings:MailWinnow configuration value is required.")));

        services.AddMailWinnowCredentialProtection(configuration);
        services.Configure<LocalImapOptions>(configuration.GetSection(LocalImapOptions.SectionName));
        services.Configure<MailSyncOptions>(configuration.GetSection(MailSyncOptions.SectionName));
        services.AddIdentityCore<ApplicationUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 12;
                options.Password.RequireNonAlphanumeric = false;
            })
            .AddRoles<IdentityRole>()
            .AddSignInManager()
            .AddEntityFrameworkStores<MailWinnowDbContext>()
            .AddDefaultTokenProviders();

        services.AddScoped<IFirstRunSetupService, FirstRunSetupService>();
        services.AddScoped<IHouseholdAccountService, HouseholdAccountService>();
        services.AddScoped<IOwnershipAuthorizer, OwnershipAuthorizer>();
        services.AddScoped<IMailboxConfigurationService, MailboxConfigurationService>();
        services.AddScoped<IImapConnectionService, ImapConnectionService>();
        services.AddScoped<ISourceMailboxSynchronizer, SourceMailboxSynchronizer>();
        services.AddSingleton<IMailSyncQueue, MailSyncQueue>();
        return services;
    }
}
