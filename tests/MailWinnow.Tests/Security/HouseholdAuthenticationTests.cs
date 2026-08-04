using System.Security.Claims;
using MailWinnow.Infrastructure.Persistence;
using MailWinnow.Infrastructure.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MailWinnow.Tests.Security;

public sealed class HouseholdAuthenticationTests
{
    [Fact]
    public async Task FirstRunCreatesOnlyFirstAdministratorAndAuthenticatesPassword()
    {
        await using var fixture = await IdentityFixture.CreateAsync();

        var first = await fixture.Setup.CreateFirstAdministratorAsync(
            "admin@example.test",
            "ValidPassword1");
        var second = await fixture.Setup.CreateFirstAdministratorAsync(
            "other@example.test",
            "AnotherPassword2");

        Assert.True(first.Succeeded, first.Error);
        Assert.False(second.Succeeded);
        Assert.False(await fixture.Setup.IsSetupRequiredAsync());
        var administrator = await fixture.Users.FindByEmailAsync("admin@example.test");
        Assert.NotNull(administrator);
        Assert.True(await fixture.Users.IsInRoleAsync(administrator, AuthConstants.AdministratorRole));
        Assert.True(await fixture.Users.CheckPasswordAsync(administrator, "ValidPassword1"));
    }

    [Fact]
    public async Task AdministratorCanCreateDisableEnableAndResetHouseholdAccount()
    {
        await using var fixture = await IdentityFixture.CreateAsync();
        Assert.True((await fixture.Setup.CreateFirstAdministratorAsync(
            "admin@example.test",
            "ValidPassword1")).Succeeded);
        var administrator = await fixture.Users.FindByEmailAsync("admin@example.test");
        Assert.NotNull(administrator);
        var actor = Principal(administrator.Id, AuthConstants.AdministratorRole);

        Assert.True((await fixture.Accounts.CreateAsync(
            actor,
            "member@example.test",
            "MemberPassword1")).Succeeded);
        var member = await fixture.Users.FindByEmailAsync("member@example.test");
        Assert.NotNull(member);

        Assert.True((await fixture.Accounts.SetEnabledAsync(actor, member.Id, enabled: false)).Succeeded);
        member = await fixture.Users.FindByIdAsync(member.Id);
        Assert.NotNull(member);
        Assert.True(member.LockoutEnd > DateTimeOffset.UtcNow);

        Assert.True((await fixture.Accounts.SetEnabledAsync(actor, member.Id, enabled: true)).Succeeded);
        Assert.True((await fixture.Accounts.ResetPasswordAsync(
            actor,
            member.Id,
            "ReplacementPassword2")).Succeeded);
        member = await fixture.Users.FindByIdAsync(member.Id);
        Assert.NotNull(member);
        Assert.Null(member.LockoutEnd);
        Assert.True(await fixture.Users.CheckPasswordAsync(member, "ReplacementPassword2"));
    }

    [Fact]
    public async Task NormalUserCannotInvokeHouseholdAdministrationService()
    {
        await using var fixture = await IdentityFixture.CreateAsync();
        await fixture.Setup.CreateFirstAdministratorAsync("admin@example.test", "ValidPassword1");
        var administrator = await fixture.Users.FindByEmailAsync("admin@example.test");
        Assert.NotNull(administrator);
        await fixture.Accounts.CreateAsync(
            Principal(administrator.Id, AuthConstants.AdministratorRole),
            "member@example.test",
            "MemberPassword1");
        var member = await fixture.Users.FindByEmailAsync("member@example.test");
        Assert.NotNull(member);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Accounts.CreateAsync(Principal(member.Id), "intruder@example.test", "InvalidPassword3"));
    }

    [Fact]
    public void OwnershipBoundaryAllowsOwner()
    {
        var authorizer = new OwnershipAuthorizer();
        authorizer.RequireOwner(Principal("owner-id"), "owner-id");
    }

    [Fact]
    public void AdministratorRoleDoesNotBypassAnotherUsersPrivateData()
    {
        var authorizer = new OwnershipAuthorizer();
        var administrator = Principal("admin-id", AuthConstants.AdministratorRole);

        Assert.Throws<UnauthorizedAccessException>(() =>
            authorizer.RequireOwner(administrator, "owner-id"));
    }

    [Fact]
    public async Task AdministratorPolicyRequiresAdministratorRole()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMailWinnowAuthorization();
        await using var provider = services.BuildServiceProvider();
        var policyProvider = provider.GetRequiredService<IAuthorizationPolicyProvider>();

        var policy = await policyProvider.GetPolicyAsync(AuthConstants.AdministratorPolicy);

        var roleRequirement = Assert.Single(
            Assert.IsType<RolesAuthorizationRequirement>(Assert.Single(policy!.Requirements))
                .AllowedRoles);
        Assert.Equal(AuthConstants.AdministratorRole, roleRequirement);
    }

    private static ClaimsPrincipal Principal(string userId, params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId),
            new(ClaimTypes.Name, userId)
        };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private sealed class IdentityFixture : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly SqliteConnection _connection;
        private readonly AsyncServiceScope _scope;

        private IdentityFixture(ServiceProvider provider, SqliteConnection connection, AsyncServiceScope scope)
        {
            _provider = provider;
            _connection = connection;
            _scope = scope;
            Users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            Setup = scope.ServiceProvider.GetRequiredService<IFirstRunSetupService>();
            Accounts = scope.ServiceProvider.GetRequiredService<IHouseholdAccountService>();
        }

        public UserManager<ApplicationUser> Users { get; }
        public IFirstRunSetupService Setup { get; }
        public IHouseholdAccountService Accounts { get; }

        public static async Task<IdentityFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDataProtection();
            services.AddDbContext<MailWinnowDbContext>(options => options.UseSqlite(connection));
            services.AddIdentityCore<ApplicationUser>(options =>
                {
                    options.User.RequireUniqueEmail = true;
                    options.Password.RequiredLength = 12;
                    options.Password.RequireNonAlphanumeric = false;
                })
                .AddRoles<IdentityRole>()
                .AddEntityFrameworkStores<MailWinnowDbContext>()
                .AddDefaultTokenProviders();
            services.AddScoped<IFirstRunSetupService, FirstRunSetupService>();
            services.AddScoped<IHouseholdAccountService, HouseholdAccountService>();
            var provider = services.BuildServiceProvider();
            var scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>().Database.EnsureCreatedAsync();
            return new IdentityFixture(provider, connection, scope);
        }

        public async ValueTask DisposeAsync()
        {
            await _scope.DisposeAsync();
            await _provider.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
