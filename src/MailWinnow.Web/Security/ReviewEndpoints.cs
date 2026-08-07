using MailWinnow.Core.Rules;
using MailWinnow.Infrastructure.Rules;
using MailWinnow.Infrastructure.Mailboxes;
using MailWinnow.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace MailWinnow.Web.Security;

public static class ReviewEndpoints
{
    public static IEndpointRouteBuilder MapReviewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/review").RequireAuthorization();
        group.MapPost("/rule", AddRuleAsync);
        group.MapPost("/rule/delete", DeleteRuleAsync);
        group.MapPost("/message/approve", ApproveAsync);
        group.MapPost("/messages/approve", ApproveManyAsync);
        group.MapPost("/message/undo", UndoAsync);
        group.MapPost("/delivery/retry", RetryDeliveryAsync);
        return endpoints;
    }

    private static async Task<IResult> AddRuleAsync(HttpContext context, [FromForm] RuleRequest request, IRuleManagementService rules, IOwnershipAuthorizer ownership, CancellationToken ct)
    {
        try
        {
            var owner = ownership.RequireCurrentUserId(context.User);
            DateTimeOffset? expiry = request.Action == RuleAction.TemporarilyAllow ? request.ExpiresUtc ?? DateTimeOffset.UtcNow.AddDays(7) : null;
            var rule = new MailRule { OwnerUserId = owner, Action = request.Action, Scope = RuleScope.User, MatchType = request.MatchType, MatchValue = request.MatchValue.Trim(), EffectiveUtc = expiry is null ? null : DateTimeOffset.UtcNow, ExpiresUtc = expiry, DeliveredMessageRetentionDays = request.RetentionDays };
            if (request.ReplaceRuleId is { } replaceRuleId)
            {
                await rules.ReplaceAsync(owner, replaceRuleId, rule, ct);
                return Redirect("Rule replaced and matching stored messages refreshed.");
            }
            await rules.AddOrUpdateAsync(rule, ct);
            return Redirect("Decision applied to matching stored messages.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return Redirect(ex.Message, true); }
    }
    private static async Task<IResult> DeleteRuleAsync(HttpContext context, [FromForm] RuleIdRequest request, IRuleManagementService rules, IOwnershipAuthorizer ownership, CancellationToken ct)
    {
        try { await rules.DeleteAsync(ownership.RequireCurrentUserId(context.User), request.Id, ct); return Redirect("Rule removed and stored messages refreshed."); }
        catch (InvalidOperationException ex) { return Redirect(ex.Message, true); }
    }
    private static async Task<IResult> ApproveAsync(HttpContext context, [FromForm] HeaderRequest request, IRuleManagementService rules, IOwnershipAuthorizer ownership, CancellationToken ct)
    {
        try { await rules.SetMessageDecisionAsync(new MessageDecision { OwnerUserId = ownership.RequireCurrentUserId(context.User), SourceMessageHeaderId = request.Id, Action = RuleAction.ApproveOneMessage }, ct); return Redirect("One-message approval applied."); }
        catch (InvalidOperationException ex) { return Redirect(ex.Message, true); }
    }
    private static async Task<IResult> ApproveManyAsync(HttpContext context, [FromForm] HeaderBatchRequest request, IRuleManagementService rules, IOwnershipAuthorizer ownership, CancellationToken ct)
    {
        try
        {
            if (request.Ids.Count == 0) throw new InvalidOperationException("No messages were selected.");
            var owner = ownership.RequireCurrentUserId(context.User);
            foreach (var id in request.Ids.Distinct())
                await rules.SetMessageDecisionAsync(new MessageDecision { OwnerUserId = owner, SourceMessageHeaderId = id, Action = RuleAction.ApproveOneMessage }, ct);
            return Redirect($"Approved {request.Ids.Distinct().Count()} messages.");
        }
        catch (InvalidOperationException ex) { return Redirect(ex.Message, true); }
    }
    private static async Task<IResult> UndoAsync(HttpContext context, [FromForm] HeaderRequest request, IRuleManagementService rules, IOwnershipAuthorizer ownership, CancellationToken ct)
    {
        try { await rules.DeleteMessageDecisionAsync(ownership.RequireCurrentUserId(context.User), request.Id, ct); return Redirect("One-message decision undone."); }
        catch (InvalidOperationException ex) { return Redirect(ex.Message, true); }
    }
    private static async Task<IResult> RetryDeliveryAsync(HttpContext context, [FromForm] DeliveryRequest request, IMessageDeliveryService deliveries, CancellationToken ct)
    {
        try
        {
            var result = await deliveries.RetryAsync(context.User, request.Id, ct);
            return Redirect(result.Message, !result.Succeeded);
        }
        catch (InvalidOperationException ex) { return Redirect(ex.Message, true); }
    }
    private static IResult Redirect(string message, bool error = false) => Results.LocalRedirect("/review?" + (error ? "error=" : "saved=") + Uri.EscapeDataString(message));
    public sealed record RuleRequest(RuleAction Action, RuleMatchType MatchType, string MatchValue, DateTimeOffset? ExpiresUtc, int? RetentionDays = null, Guid? ReplaceRuleId = null);
    public sealed record RuleIdRequest(Guid Id);
    public sealed record HeaderRequest(Guid Id);
    public sealed class HeaderBatchRequest { public List<Guid> Ids { get; set; } = []; }
    public sealed record DeliveryRequest(Guid Id);
}
