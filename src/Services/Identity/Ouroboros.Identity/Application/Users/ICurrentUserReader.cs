using Ouroboros.Identity.Contracts.Users;
using Ouroboros.Identity.Domain.Users;

namespace Ouroboros.Identity.Application.Users;

internal interface ICurrentUserReader
{
    Task<CurrentUser?> FindActiveAsync(Guid externalId, UserRole role, CancellationToken cancellationToken);
}
