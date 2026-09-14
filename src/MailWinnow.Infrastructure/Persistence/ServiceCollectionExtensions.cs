using MailWinnow.Infrastructure.Security;
using MailWinnow.Infrastructure.Mailboxes;
using MailWinnow.Infrastructure.Rules;
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
        services.AddScoped<ILocalImapHealthChecker, LocalImapHealthChecker>();
        services.AddScoped<ISourceMailboxSyncLockProvider, SqlServerAccountLockProvider>();
        services.AddScoped<ISourceMailboxSynchronizer, SourceMailboxSynchronizer>();
        services.AddScoped<IRuleEvaluationService, RuleEvaluationService>();
        services.AddScoped<IRuleImpactPreviewService, RuleImpactPreviewService>();
        services.AddScoped<IRuleManagementService, RuleManagementService>();
        services.AddScoped<MessageReviewService>();
        services.AddScoped<IMessageReviewService, ScopedMessageReviewService>();
        services.AddScoped<INavigationCountService, NavigationCountService>();
        services.AddScoped<NavigationProjectionRefreshService>();
        services.AddScoped<IReviewMessagePreviewService, ReviewMessagePreviewService>();
        services.AddSingleton<ReviewDecisionQueue>();
        services.AddSingleton<IReviewDecisionQueue>(provider => provider.GetRequiredService<ReviewDecisionQueue>());
        services.AddScoped<IInboxReaderService, InboxReaderService>();
        services.AddSingleton<InboxDeletionQueue>();
        services.AddSingleton<IInboxDeletionQueue>(provider => provider.GetRequiredService<InboxDeletionQueue>());
        services.AddScoped<IMessageDeliveryService, MessageDeliveryService>();
        services.AddScoped<IBlockedMessageDeletionService, BlockedMessageDeletionService>();
        services.AddScoped<IMailboxRetentionService, MailboxRetentionService>();
        services.AddScoped<IAdministrationService, AdministrationService>();
        services.AddScoped<IAuditRecorder>(provider => provider.GetRequiredService<IAdministrationService>());
        services.AddSingleton<IMailSyncQueue, MailSyncQueue>();
        services.AddSingleton<IInboxRefreshService, InboxRefreshService>();
        services.AddSingleton<MailSyncWakeSignal>();
        return services;
    }
}
