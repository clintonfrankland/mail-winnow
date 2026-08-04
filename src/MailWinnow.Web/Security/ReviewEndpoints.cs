using MailWinnow.Core.Rules;
using MailWinnow.Infrastructure.Rules;
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
        group.MapPost("/message/undo", UndoAsync);
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
    private static async Task<IResult> UndoAsync(HttpContext context, [FromForm] HeaderRequest request, IRuleManagementService rules, IOwnershipAuthorizer ownership, CancellationToken ct)
    {
        try { await rules.DeleteMessageDecisionAsync(ownership.RequireCurrentUserId(context.User), request.Id, ct); return Redirect("One-message decision undone."); }
        catch (InvalidOperationException ex) { return Redirect(ex.Message, true); }
    }
    private static IResult Redirect(string message, bool error = false) => Results.LocalRedirect("/review?" + (error ? "error=" : "saved=") + Uri.EscapeDataString(message));
    public sealed record RuleRequest(RuleAction Action, RuleMatchType MatchType, string MatchValue, DateTimeOffset? ExpiresUtc, int RetentionDays = MailRule.DefaultDeliveredMessageRetentionDays, Guid? ReplaceRuleId = null);
    public sealed record RuleIdRequest(Guid Id);
    public sealed record HeaderRequest(Guid Id);
}
