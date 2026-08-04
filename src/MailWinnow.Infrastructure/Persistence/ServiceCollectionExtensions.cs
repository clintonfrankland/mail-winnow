using MailWinnow.Infrastructure.Security;
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
        return services;
    }
}
