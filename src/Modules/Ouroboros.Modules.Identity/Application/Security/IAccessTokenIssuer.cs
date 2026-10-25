using Ouroboros.Modules.Identity.Domain.Users;

namespace Ouroboros.Modules.Identity.Application.Security;

internal interface IAccessTokenIssuer
{
    AccessToken Issue(Guid userId, UserRole role, Guid sessionId);
}
