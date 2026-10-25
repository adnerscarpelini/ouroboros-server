using Ouroboros.Identity.Application.Sessions;
using Ouroboros.Identity.Contracts.Users;

namespace Ouroboros.Identity.Application.Users;

internal sealed class GetCurrentUser(ICurrentUserReader reader) : IGetCurrentUser
{
    public Task<CurrentUser?> ExecuteAsync(GetCurrentUserRequest request, CancellationToken cancellationToken)
    {
        // Papel alterado no banco depois da emissão do token também deixa de ser aceito, como na renovação.
        var role = LoginChannels.RoleOf(request.Channel);
        return reader.FindActiveAsync(request.UserId, role, cancellationToken);
    }
}
