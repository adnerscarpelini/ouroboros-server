using Microsoft.EntityFrameworkCore;
using Ouroboros.Modules.Identity.Application.Sessions;
using Ouroboros.Modules.Identity.Contracts.Users;
using Ouroboros.Modules.Identity.Infrastructure.Persistence;

namespace Ouroboros.Modules.Identity.Application.Users;

internal sealed class GetCurrentUser(IdentityDbContext context) : IGetCurrentUser
{
    public Task<CurrentUser?> ExecuteAsync(GetCurrentUserRequest request, CancellationToken cancellationToken)
    {
        // Papel alterado no banco depois da emissão do token também deixa de ser aceito, como na renovação.
        var role = LoginChannels.RoleOf(request.Channel);
        return context.Users
            .AsNoTracking()
            .Where(user => user.ExternalId == request.UserId && user.IsActive && user.Role == role)
            .Select(user => new CurrentUser(user.ExternalId, user.Email, user.Role.ToString()))
            .SingleOrDefaultAsync(cancellationToken);
    }
}
