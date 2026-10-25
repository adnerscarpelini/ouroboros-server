using Microsoft.EntityFrameworkCore;
using Ouroboros.Modules.Identity.Application.Users;
using Ouroboros.Modules.Identity.Contracts.Users;
using Ouroboros.Modules.Identity.Domain.Users;

namespace Ouroboros.Modules.Identity.Infrastructure.Persistence;

internal sealed class CurrentUserReader(IdentityDbContext context) : ICurrentUserReader
{
    public Task<CurrentUser?> FindActiveAsync(Guid externalId, UserRole role, CancellationToken cancellationToken) =>
        context.Users
            .AsNoTracking()
            .Where(user => user.ExternalId == externalId && user.IsActive && user.Role == role)
            .Select(user => new CurrentUser(user.ExternalId, user.Email, user.Role.ToString()))
            .SingleOrDefaultAsync(cancellationToken);
}
