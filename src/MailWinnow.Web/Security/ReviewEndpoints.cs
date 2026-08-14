using MailWinnow.Core.Rules;
using MailWinnow.Infrastructure.Rules;
using MailWinnow.Infrastructure.Mailboxes;
using MailWinnow.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.DataProtection;
using System.Text.Json;

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

    internal static async Task<IResult> AddRuleAsync(HttpContext context, [FromForm] RuleRequest request, IRuleManagementService rules, IRuleImpactPreviewService previews, IOwnershipAuthorizer ownership, IDataProtectionProvider protection, CancellationToken ct)
    {
        try
        {
            var owner = ownership.RequireCurrentUserId(context.User);
            var now = DateTimeOffset.UtcNow;
            var rule = BuildRule(owner, request, now);
            var protector = protection.CreateProtector("MailWinnow.RulePreview.v1").ToTimeLimitedDataProtector();
            var snapshot = Snapshot.From(request, rule, owner);
            if (!request.Confirm)
            {
                var impact = await previews.PreviewAsync(owner, rule, request.ReplaceRuleId, now, ct);
                snapshot = snapshot with { Impact = impact };
                var token = protector.Protect(JsonSerializer.Serialize(snapshot), TimeSpan.FromMinutes(15));
                return Results.LocalRedirect("/rules?preview=" + Uri.EscapeDataString(token));
            }
            if (string.IsNullOrWhiteSpace(request.PreviewToken)) throw new InvalidOperationException("Preview the rule before confirming it.");
            Snapshot previewed;
            try { previewed = JsonSerializer.Deserialize<Snapshot>(protector.Unprotect(request.PreviewToken))!; }
            catch { throw new InvalidOperationException("The rule preview is stale or invalid. Preview it again."); }
            if (previewed is null || previewed.ProposalRuleId == Guid.Empty || previewed.OwnerUserId != owner || !previewed.SameConfiguration(snapshot))
                throw new InvalidOperationException("The rule inputs changed after preview. Preview them again.");
            if (request.ReplaceRuleId is { } replaceRuleId)
            {
                await rules.ReplaceAsync(owner, replaceRuleId, rule, ct);
                return Redirect(context, "Rule updated and matching stored messages refreshed.");
            }
            rule.Id = previewed.ProposalRuleId;
            await rules.AddOrUpdateAsync(rule, ct);
            return Redirect(context, "Rule created and matching stored messages refreshed.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return Redirect(context, ex.Message, true); }
    }
    internal static MailRule BuildRule(string owner, RuleRequest request, DateTimeOffset now)
    {
        DateTimeOffset? expiry = request.Action == RuleAction.TemporarilyAllow ? request.ExpiresUtc ?? now.AddDays(7) : null;
        DateTimeOffset? effective = expiry is null ? null : request.Confirm ? request.EffectiveUtc ?? now : now;
        return new MailRule { OwnerUserId = owner, Action = request.Action, Scope = request.Scope, MatchType = request.MatchType,
            MatchValue = request.MatchValue.Trim(), SourceMailboxId = request.Scope == RuleScope.SourceAccount ? request.SourceMailboxId : null,
            EffectiveUtc = effective, ExpiresUtc = expiry, DeliveredMessageRetentionDays = request.RetentionDays };
    }
    private static async Task<IResult> DeleteRuleAsync(HttpContext context, [FromForm] RuleIdRequest request, IRuleManagementService rules, IOwnershipAuthorizer ownership, CancellationToken ct)
    {
        try { await rules.DeleteAsync(ownership.RequireCurrentUserId(context.User), request.Id, ct); return Redirect(context, "Rule removed and stored messages refreshed."); }
        catch (InvalidOperationException ex) { return Redirect(context, ex.Message, true); }
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
    private static IResult Redirect(HttpContext context, string message, bool error = false)
    {
        var path = string.Equals(context.Request.Query["returnUrl"], "/rules", StringComparison.Ordinal) ? "/rules" : "/review";
        return Results.LocalRedirect(path + "?" + (error ? "error=" : "saved=") + Uri.EscapeDataString(message));
    }
    public sealed class RuleRequest
    {
        public RuleAction Action { get; set; }
        public RuleScope Scope { get; set; } = RuleScope.User;
        public RuleMatchType MatchType { get; set; }
        public string MatchValue { get; set; } = string.Empty;
        public Guid? SourceMailboxId { get; set; }
        public DateTimeOffset? ExpiresUtc { get; set; }
        public DateTimeOffset? EffectiveUtc { get; set; }
        public int? RetentionDays { get; set; }
        public Guid? ReplaceRuleId { get; set; }
        public bool Confirm { get; set; }
        public string? PreviewToken { get; set; }
    }
    public sealed record Snapshot(Guid ProposalRuleId, string OwnerUserId, RuleAction Action, RuleScope Scope, RuleMatchType MatchType, string MatchValue,
        Guid? SourceMailboxId, DateTimeOffset? EffectiveUtc, DateTimeOffset? ExpiresUtc, int? RetentionDays, Guid? ReplaceRuleId, RuleImpactPreview? Impact = null)
    {
        public static Snapshot From(RuleRequest request, MailRule rule, string owner) => new(rule.Id, owner, rule.Action, rule.Scope, rule.MatchType,
            rule.MatchValue, rule.SourceMailboxId, rule.EffectiveUtc, rule.ExpiresUtc, rule.DeliveredMessageRetentionDays, request.ReplaceRuleId);
        public bool SameConfiguration(Snapshot other) => (this with { ProposalRuleId = Guid.Empty, Impact = null }) == (other with { ProposalRuleId = Guid.Empty, Impact = null });
    }
    public sealed record RuleIdRequest(Guid Id);
    public sealed record HeaderRequest(Guid Id);
    public sealed class HeaderBatchRequest { public List<Guid> Ids { get; set; } = []; }
    public sealed record DeliveryRequest(Guid Id);
}
