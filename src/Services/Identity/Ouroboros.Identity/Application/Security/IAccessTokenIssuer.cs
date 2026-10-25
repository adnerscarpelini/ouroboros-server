using Ouroboros.Identity.Domain.Users;

namespace Ouroboros.Identity.Application.Security;

internal interface IAccessTokenIssuer
{
    AccessToken Issue(Guid userId, UserRole role, Guid sessionId);
}
