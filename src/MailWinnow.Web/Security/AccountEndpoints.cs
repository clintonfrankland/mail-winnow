using MailWinnow.Infrastructure.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace MailWinnow.Web.Security;

public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/auth/setup", SetupAsync).AllowAnonymous();
        endpoints.MapGet("/auth/login", LegacyLoginRedirect).AllowAnonymous();
        endpoints.MapPost("/auth/login", LoginAsync).AllowAnonymous();
        endpoints.MapPost("/auth/logout", LogoutAsync).RequireAuthorization();

        var household = endpoints.MapGroup("/auth/household")
            .RequireAuthorization(AuthConstants.AdministratorPolicy);
        household.MapPost("/create", CreateAsync);
        household.MapPost("/enabled", SetEnabledAsync);
        household.MapPost("/password", ResetPasswordAsync);
        return endpoints;
    }

    internal static IResult LegacyLoginRedirect() => Results.LocalRedirect("/login");

    private static async Task<IResult> SetupAsync(
        [FromForm] SetupRequest request,
        IFirstRunSetupService setupService,
        SignInManager<ApplicationUser> signInManager,
        UserManager<ApplicationUser> userManager,
        CancellationToken cancellationToken)
    {
        var result = await setupService.CreateFirstAdministratorAsync(
            request.Email,
            request.Password,
            cancellationToken);
        if (!result.Succeeded)
        {
            return RedirectWithMessage("/setup", result.Error);
        }

        var user = await userManager.FindByEmailAsync(request.Email.Trim());
        if (user is not null)
        {
            await signInManager.SignInAsync(user, isPersistent: false);
        }

        return Results.LocalRedirect("/");
    }

    private static async Task<IResult> LoginAsync(
        [FromForm] LoginRequest request,
        SignInManager<ApplicationUser> signInManager)
    {
        var result = await signInManager.PasswordSignInAsync(
            request.Email.Trim(),
            request.Password,
            request.RememberMe,
            lockoutOnFailure: true);
        return result.Succeeded
            ? Results.LocalRedirect("/")
            : RedirectWithMessage("/login", "Sign-in failed.");
    }

    private static async Task<IResult> LogoutAsync(SignInManager<ApplicationUser> signInManager)
    {
        await signInManager.SignOutAsync();
        return Results.LocalRedirect("/login");
    }

    private static async Task<IResult> CreateAsync(
        HttpContext context,
        [FromForm] SetupRequest request,
        IHouseholdAccountService accounts, IAdministrationService audit)
    {
        var result = await accounts.CreateAsync(context.User, request.Email, request.Password);
        if (result.Succeeded) await audit.RecordAsync("user.created", context.User.FindFirstValue(ClaimTypes.NameIdentifier), resourceType: "user");
        return RedirectWithMessage("/admin/household", result.Error);
    }

    private static async Task<IResult> SetEnabledAsync(
        HttpContext context,
        [FromForm] EnabledRequest request,
        IHouseholdAccountService accounts, IAdministrationService audit)
    {
        var result = await accounts.SetEnabledAsync(context.User, request.UserId, request.Enabled);
        if (result.Succeeded) await audit.RecordAsync(request.Enabled ? "user.enabled" : "user.disabled", context.User.FindFirstValue(ClaimTypes.NameIdentifier), request.UserId, "user", request.UserId);
        return RedirectWithMessage("/admin/household", result.Error);
    }

    private static async Task<IResult> ResetPasswordAsync(
        HttpContext context,
        [FromForm] PasswordRequest request,
        IHouseholdAccountService accounts, IAdministrationService audit)
    {
        var result = await accounts.ResetPasswordAsync(context.User, request.UserId, request.Password);
        if (result.Succeeded) await audit.RecordAsync("user.passwordReset", context.User.FindFirstValue(ClaimTypes.NameIdentifier), request.UserId, "user", request.UserId);
        return RedirectWithMessage("/admin/household", result.Error);
    }

    private static IResult RedirectWithMessage(string path, string? error)
    {
        var suffix = string.IsNullOrWhiteSpace(error)
            ? "?saved=true"
            : $"?error={Uri.EscapeDataString(error)}";
        return Results.LocalRedirect(path + suffix);
    }

    public sealed record SetupRequest(string Email, string Password);
    public sealed class LoginRequest
    {
        public string Email { get; init; } = string.Empty;
        public string Password { get; init; } = string.Empty;
        public bool RememberMe { get; init; }
    }
    public sealed record EnabledRequest(string UserId, bool Enabled);
    public sealed record PasswordRequest(string UserId, string Password);
}
