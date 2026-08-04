using Microsoft.Extensions.DependencyInjection;

namespace MailWinnow.Infrastructure.Security;

public static class AuthorizationServiceCollectionExtensions
{
    public static IServiceCollection AddMailWinnowAuthorization(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            .AddPolicy(AuthConstants.AdministratorPolicy,
                policy => policy.RequireRole(AuthConstants.AdministratorRole));
        return services;
    }
}
