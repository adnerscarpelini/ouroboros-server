using Ouroboros.Modules.Identity.Contracts.Users;
using Ouroboros.Modules.Identity.Domain.Users;

namespace Ouroboros.Modules.Identity.Application.Users;

internal interface ICurrentUserReader
{
    Task<CurrentUser?> FindActiveAsync(Guid externalId, UserRole role, CancellationToken cancellationToken);
}
