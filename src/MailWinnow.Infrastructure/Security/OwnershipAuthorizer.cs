using System.Security.Claims;
using Microsoft.AspNetCore.Identity;

namespace MailWinnow.Infrastructure.Security;

/// <summary>
/// The mandatory service-layer ownership boundary for mail accounts, rules, message headers,
/// and credentials. Administrator role membership intentionally does not bypass ownership.
/// </summary>
public interface IOwnershipAuthorizer
{
    string RequireCurrentUserId(ClaimsPrincipal actor);
    void RequireOwner(ClaimsPrincipal actor, string ownerUserId);
}

public sealed class OwnershipAuthorizer : IOwnershipAuthorizer
{
    public string RequireCurrentUserId(ClaimsPrincipal actor) =>
        actor.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new UnauthorizedAccessException("An authenticated user is required.");

    public void RequireOwner(ClaimsPrincipal actor, string ownerUserId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerUserId);
        if (!string.Equals(RequireCurrentUserId(actor), ownerUserId, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("The requested private mail data belongs to another user.");
        }
    }
}
