using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;

namespace MailWinnow.Infrastructure.Rules;

/// <summary>Blazor circuits retain this facade, never an EF context or its change tracker.</summary>
public sealed class ScopedMessageReviewService(IServiceScopeFactory scopes) : IMessageReviewService
{
    public Task<IReadOnlyList<MessageReviewItem>> GetRecentAsync(ClaimsPrincipal user, MessageReviewFilter filter, CancellationToken cancellationToken = default) =>
        ReadAsync(service => service.GetRecentAsync(user, filter, cancellationToken));

    public Task<IReadOnlyList<MessageReviewGroup>> GetBySenderAsync(ClaimsPrincipal user, MessageReviewFilter filter, CancellationToken cancellationToken = default) =>
        ReadAsync(service => service.GetBySenderAsync(user, filter, cancellationToken));

    public Task<IReadOnlyList<MessageReviewGroup>> GetBySubjectAsync(ClaimsPrincipal user, MessageReviewFilter filter, CancellationToken cancellationToken = default) =>
        ReadAsync(service => service.GetBySubjectAsync(user, filter, cancellationToken));

    public Task<IReadOnlyList<ReviewRule>> GetRulesAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default) =>
        ReadAsync(service => service.GetRulesAsync(user, cancellationToken));

    private async Task<T> ReadAsync<T>(Func<MessageReviewService, Task<T>> read)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await read(scope.ServiceProvider.GetRequiredService<MessageReviewService>());
    }
}
