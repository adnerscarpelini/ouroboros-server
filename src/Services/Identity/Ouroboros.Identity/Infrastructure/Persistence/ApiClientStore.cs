using Microsoft.EntityFrameworkCore;
using Ouroboros.Identity.Application.ApiClients;
using Ouroboros.Identity.Domain.ApiClients;
using Ouroboros.Identity.Domain.Notifications;

namespace Ouroboros.Identity.Infrastructure.Persistence;

internal sealed class ApiClientStore(IdentityDbContext context) : IApiClientStore
{
    public Task<ApiClient?> FindAsync(Guid clientId, CancellationToken cancellationToken) =>
        context.ApiClients.SingleOrDefaultAsync(client => client.ExternalId == clientId, cancellationToken);
    public void Add(ApiClient client) => context.ApiClients.Add(client);
    public void AddIntents(IEnumerable<EmailIntent> intents) => context.EmailIntents.AddRange(intents);
    public async Task SaveAsync(CancellationToken cancellationToken) => await context.SaveChangesAsync(cancellationToken);
}
