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
        group.MapPost("/source/folders/save", SaveSourceFoldersAsync);
        group.MapPost("/source/sync", QueueSyncAsync);
        group.MapPost("/destination/save", SaveDestinationAsync);
        return endpoints;
    }

    private static async Task<IResult> SaveSourceAsync(HttpContext context, [FromForm] SourceRequest request, IMailboxConfigurationService service, CancellationToken ct) =>
        Redirect(await service.SaveSourceAsync(context.User, request.Id, new(request.DisplayName, request.Host, request.Port, request.UseSsl, request.Username, request.Password, request.Enabled, ParseFolders(request.SelectedFolders)), ct));
    private static async Task<IResult> SetSourceEnabledAsync(HttpContext context, [FromForm] SourceEnabledRequest request, IMailboxConfigurationService service, CancellationToken ct) =>
        Redirect(await service.SetSourceEnabledAsync(context.User, request.Id, request.Enabled, ct));
    private static async Task<IResult> TestSourceAsync(HttpContext context, [FromForm] SourceTestRequest request, IMailboxConfigurationService service, CancellationToken ct)
    {
        var result = await service.TestSourceAsync(context.User, request.Id, request.DiscoverFolders, ct);
        return Results.LocalRedirect(BuildRedirectUrl(result, result.Succeeded && request.DiscoverFolders ? request.Id : null));
    }
    private static async Task<IResult> SaveSourceFoldersAsync(HttpContext context, [FromForm] SourceFoldersRequest request, IMailboxConfigurationService service, CancellationToken ct) =>
        Redirect(await service.SaveSourceFoldersAsync(context.User, request.Id, request.SelectedFolders, ct));
    private static async Task<IResult> QueueSyncAsync(HttpContext context, [FromForm] SourceEnabledRequest request, IMailSyncQueue queue, CancellationToken ct) =>
        Redirect(await queue.RequestAsync(context.User, request.Id, ct));
    private static async Task<IResult> SaveDestinationAsync(HttpContext context, [FromForm] DestinationRequest request, IMailboxConfigurationService service, CancellationToken ct) =>
        Redirect(await service.SaveDestinationAsync(context.User, new(request.Username, request.Password, request.Folder, request.Enabled), ct));

    private static IResult Redirect(MailboxOperationResult result) => Results.LocalRedirect("/mailboxes?" + (result.Succeeded ? "saved=" : "error=") + Uri.EscapeDataString(result.Message));
    internal static string BuildRedirectUrl(MailboxOperationResult result, Guid? discoveredSourceId)
    {
        var query = new List<string> { (result.Succeeded ? "saved=" : "error=") + Uri.EscapeDataString(result.Message) };
        if (discoveredSourceId is not null && result.Folders is not null)
        {
            query.Add("discoveredSourceId=" + discoveredSourceId.Value);
            query.AddRange(result.Folders.Select(folder => "discoveredFolder=" + Uri.EscapeDataString(folder)));
        }
        return "/mailboxes?" + string.Join('&', query);
    }
    private static IReadOnlyList<string> ParseFolders(string? folders) => string.IsNullOrWhiteSpace(folders) ? [] : folders.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public sealed class SourceRequest
    {
        public Guid? Id { get; init; }
        public string DisplayName { get; init; } = "";
        public string Host { get; init; } = "";
        public int Port { get; init; }
        public bool UseSsl { get; init; }
        public string Username { get; init; } = "";
        public string? Password { get; init; }
        public bool Enabled { get; init; }
        public string? SelectedFolders { get; init; }
    }

    public sealed class SourceEnabledRequest
    {
        public Guid Id { get; init; }
        public bool Enabled { get; init; }
    }

    public sealed class SourceTestRequest
    {
        public Guid Id { get; init; }
        public bool DiscoverFolders { get; init; }
    }

    public sealed class SourceFoldersRequest
    {
        public Guid Id { get; init; }
        public string[]? SelectedFolders { get; init; }
    }

    public sealed class DestinationRequest
    {
        public string Username { get; init; } = "";
        public string? Password { get; init; }
        public string Folder { get; init; } = "";
        public bool Enabled { get; init; }
    }
}
