using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MailWinnow.Infrastructure.Security;

public sealed record HouseholdAccount(string Id, string Email, bool IsEnabled, bool IsAdministrator);

public interface IHouseholdAccountService
{
    Task<IReadOnlyList<HouseholdAccount>> ListAsync(ClaimsPrincipal actor);
    Task<ServiceResult> CreateAsync(ClaimsPrincipal actor, string email, string password);
    Task<ServiceResult> SetEnabledAsync(ClaimsPrincipal actor, string userId, bool enabled);
    Task<ServiceResult> ResetPasswordAsync(ClaimsPrincipal actor, string userId, string newPassword);
}

public sealed class HouseholdAccountService(UserManager<ApplicationUser> userManager)
    : IHouseholdAccountService
{
    public async Task<IReadOnlyList<HouseholdAccount>> ListAsync(ClaimsPrincipal actor)
    {
        await RequireAdministratorAsync(actor);
        var users = await userManager.Users.AsNoTracking().OrderBy(user => user.Email).ToListAsync();
        var accounts = new List<HouseholdAccount>(users.Count);
        foreach (var user in users)
        {
            accounts.Add(new HouseholdAccount(
                user.Id,
                user.Email ?? user.UserName ?? user.Id,
                !IsDisabled(user),
                await userManager.IsInRoleAsync(user, AuthConstants.AdministratorRole)));
        }

        return accounts;
    }

    public async Task<ServiceResult> CreateAsync(ClaimsPrincipal actor, string email, string password)
    {
        await RequireAdministratorAsync(actor);
        var normalizedEmail = email.Trim();
        var user = new ApplicationUser
        {
            UserName = normalizedEmail,
            Email = normalizedEmail,
            EmailConfirmed = true
        };
        return ToServiceResult(await userManager.CreateAsync(user, password));
    }

    public async Task<ServiceResult> SetEnabledAsync(ClaimsPrincipal actor, string userId, bool enabled)
    {
        var administrator = await RequireAdministratorAsync(actor);
        var user = await userManager.FindByIdAsync(userId);
        if (user is null)
        {
            return ServiceResult.Failure("Household account was not found.");
        }

        if (!enabled && user.Id == administrator.Id)
        {
            return ServiceResult.Failure("You cannot disable your own administrator account.");
        }

        user.LockoutEnabled = true;
        user.LockoutEnd = enabled ? null : DateTimeOffset.MaxValue;
        return ToServiceResult(await userManager.UpdateAsync(user));
    }

    public async Task<ServiceResult> ResetPasswordAsync(
        ClaimsPrincipal actor,
        string userId,
        string newPassword)
    {
        await RequireAdministratorAsync(actor);
        var user = await userManager.FindByIdAsync(userId);
        if (user is null)
        {
            return ServiceResult.Failure("Household account was not found.");
        }

        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        return ToServiceResult(await userManager.ResetPasswordAsync(user, token, newPassword));
    }

    private async Task<ApplicationUser> RequireAdministratorAsync(ClaimsPrincipal actor)
    {
        var user = await userManager.GetUserAsync(actor);
        if (user is null || IsDisabled(user) ||
            !await userManager.IsInRoleAsync(user, AuthConstants.AdministratorRole))
        {
            throw new UnauthorizedAccessException("Household administrator access is required.");
        }

        return user;
    }

    private static bool IsDisabled(ApplicationUser user) =>
        user.LockoutEnd is { } lockoutEnd && lockoutEnd > DateTimeOffset.UtcNow;

    private static ServiceResult ToServiceResult(IdentityResult result) => result.Succeeded
        ? ServiceResult.Success()
        : ServiceResult.Failure(string.Join(" ", result.Errors.Select(error => error.Description)));
}
