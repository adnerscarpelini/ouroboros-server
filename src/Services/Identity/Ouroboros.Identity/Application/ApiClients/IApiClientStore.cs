using Ouroboros.Identity.Domain.ApiClients;
using Ouroboros.Identity.Domain.Notifications;

namespace Ouroboros.Identity.Application.ApiClients;

internal interface IApiClientStore
{
    Task<ApiClient?> FindAsync(Guid clientId, CancellationToken cancellationToken);
    Task<ApiClient?> ReadCredentialAsync(Guid clientId, CancellationToken cancellationToken);
    Task<bool> IsActiveAsync(Guid clientId, CancellationToken cancellationToken);
    Task LockAsync(Guid clientId, CancellationToken cancellationToken);
    void Add(ApiClient client);
    void AddIntents(IEnumerable<EmailIntent> intents);
    Task SaveAsync(CancellationToken cancellationToken);
}
