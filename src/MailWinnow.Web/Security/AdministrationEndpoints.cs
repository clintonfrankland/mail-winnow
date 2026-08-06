using MailWinnow.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace MailWinnow.Web.Security;

public static class AdministrationEndpoints
{
    public static IEndpointRouteBuilder MapAdministrationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/operations").RequireAuthorization(AuthConstants.AdministratorPolicy);
        group.MapPost("/sync", QueueSyncAsync);
        group.MapPost("/mailbox-enabled", SetMailboxEnabledAsync);
        group.MapPost("/retry-delivery", RetryDeliveryAsync);
        return endpoints;
    }
    private static async Task<IResult> QueueSyncAsync(HttpContext context, [FromForm] IdRequest request, IAdministrationService service, CancellationToken ct) => Redirect(await service.QueueSyncAsync(context.User, request.Id, ct));
    private static async Task<IResult> SetMailboxEnabledAsync(HttpContext context, [FromForm] EnabledRequest request, IAdministrationService service, CancellationToken ct) => Redirect(await service.SetMailboxEnabledAsync(context.User, request.Id, request.Enabled, ct));
    private static async Task<IResult> RetryDeliveryAsync(HttpContext context, [FromForm] IdRequest request, IAdministrationService service, CancellationToken ct) => Redirect(await service.RetryDeliveryAsync(context.User, request.Id, ct));
    private static IResult Redirect(ServiceResult result) => Results.LocalRedirect("/admin/operations?" + (result.Succeeded ? "saved=true" : "error=" + Uri.EscapeDataString(result.Error ?? "Operation failed.")));
    public sealed record IdRequest(Guid Id);
    public sealed record EnabledRequest(Guid Id, bool Enabled);
}
