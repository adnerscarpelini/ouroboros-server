using Ouroboros.Identity.Application.Sessions;
using Ouroboros.Identity.Contracts.ApiClients;
using Ouroboros.Identity.Domain.Users;

namespace Ouroboros.Identity.Application.ApiClients;

internal sealed class GetApiClient(ISessionStore sessions, IApiClientStore store) : IGetApiClient
{
    public async Task<ApiClientResult> ExecuteAsync(Guid actorId, Guid clientId, CancellationToken cancellationToken)
    {
        var actor = await sessions.FindUserAsync(actorId, cancellationToken);
        if (actor is null || !actor.IsActive || actor.Role != UserRole.Admin) { return new(ApiClientOutcome.Forbidden); }
        var client = await store.FindAsync(clientId, cancellationToken);
        return client is null ? new(ApiClientOutcome.NotFound) : new(ApiClientOutcome.Found, RegisterApiClient.Model(client));
    }
}
