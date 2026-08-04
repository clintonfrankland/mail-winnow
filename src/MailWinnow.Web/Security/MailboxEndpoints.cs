using MailWinnow.Infrastructure.Mailboxes;
using Microsoft.AspNetCore.Mvc;

namespace MailWinnow.Web.Security;

public static class MailboxEndpoints
{
    public static IEndpointRouteBuilder MapMailboxEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/mailboxes").RequireAuthorization();
        group.MapPost("/source/save", SaveSourceAsync);
        group.MapPost("/source/enabled", SetSourceEnabledAsync);
        group.MapPost("/source/test", TestSourceAsync);
        group.MapPost("/destination/save", SaveDestinationAsync);
        return endpoints;
    }

    private static async Task<IResult> SaveSourceAsync(HttpContext context, [FromForm] SourceRequest request, IMailboxConfigurationService service, CancellationToken ct) =>
        Redirect(await service.SaveSourceAsync(context.User, request.Id, new(request.DisplayName, request.Host, request.Port, request.UseSsl, request.Username, request.Password, request.Enabled, ParseFolders(request.SelectedFolders)), ct));
    private static async Task<IResult> SetSourceEnabledAsync(HttpContext context, [FromForm] SourceEnabledRequest request, IMailboxConfigurationService service, CancellationToken ct) =>
        Redirect(await service.SetSourceEnabledAsync(context.User, request.Id, request.Enabled, ct));
    private static async Task<IResult> TestSourceAsync(HttpContext context, [FromForm] SourceTestRequest request, IMailboxConfigurationService service, CancellationToken ct) =>
        Redirect(await service.TestSourceAsync(context.User, request.Id, request.DiscoverFolders, ct));
    private static async Task<IResult> SaveDestinationAsync(HttpContext context, [FromForm] DestinationRequest request, IMailboxConfigurationService service, CancellationToken ct) =>
        Redirect(await service.SaveDestinationAsync(context.User, new(request.Username, request.Password, request.Folder, request.Enabled), ct));

    private static IResult Redirect(MailboxOperationResult result) => Results.LocalRedirect("/mailboxes?" + (result.Succeeded ? "saved=" : "error=") + Uri.EscapeDataString(result.Message));
    private static IReadOnlyList<string> ParseFolders(string? folders) => string.IsNullOrWhiteSpace(folders) ? [] : folders.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public sealed record SourceRequest(Guid? Id, string DisplayName, string Host, int Port, bool UseSsl, string Username, string? Password, bool Enabled, string? SelectedFolders);
    public sealed record SourceEnabledRequest(Guid Id, bool Enabled);
    public sealed record SourceTestRequest(Guid Id, bool DiscoverFolders);
    public sealed record DestinationRequest(string Username, string? Password, string Folder, bool Enabled);
}
