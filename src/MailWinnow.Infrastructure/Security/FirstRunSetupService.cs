using System.Data;
using MailWinnow.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MailWinnow.Infrastructure.Security;

public interface IFirstRunSetupService
{
    Task<bool> IsSetupRequiredAsync(CancellationToken cancellationToken = default);
    Task<ServiceResult> CreateFirstAdministratorAsync(
        string email,
        string password,
        CancellationToken cancellationToken = default);
}

public sealed class FirstRunSetupService(
    MailWinnowDbContext dbContext,
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole> roleManager,
    IAuditRecorder? audit = null) : IFirstRunSetupService
{
    public async Task<bool> IsSetupRequiredAsync(CancellationToken cancellationToken = default) =>
        !await dbContext.Users.AsNoTracking().AnyAsync(cancellationToken);

    public async Task<ServiceResult> CreateFirstAdministratorAsync(
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return ServiceResult.Failure("Email is required.");
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        if (await dbContext.Users.AnyAsync(cancellationToken))
        {
            return ServiceResult.Failure("First-run setup is no longer available.");
        }

        if (!await roleManager.RoleExistsAsync(AuthConstants.AdministratorRole))
        {
            var roleResult = await roleManager.CreateAsync(new IdentityRole(AuthConstants.AdministratorRole));
            if (!roleResult.Succeeded)
            {
                return ServiceResult.Failure(JoinErrors(roleResult));
            }
        }

        var normalizedEmail = email.Trim();
        var user = new ApplicationUser
        {
            UserName = normalizedEmail,
            Email = normalizedEmail,
            EmailConfirmed = true
        };
        var createResult = await userManager.CreateAsync(user, password);
        if (!createResult.Succeeded)
        {
            return ServiceResult.Failure(JoinErrors(createResult));
        }

        var roleAssignment = await userManager.AddToRoleAsync(user, AuthConstants.AdministratorRole);
        if (!roleAssignment.Succeeded)
        {
            return ServiceResult.Failure(JoinErrors(roleAssignment));
        }

        if (audit is not null)
        {
            await audit.RecordAsync("user.administrator.created", user.Id, user.Id, "user", user.Id, cancellationToken: cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return ServiceResult.Success();
    }

    private static string JoinErrors(IdentityResult result) =>
        string.Join(" ", result.Errors.Select(error => error.Description));
}
