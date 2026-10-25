using Ouroboros.Modules.Identity.Contracts.Sessions;

namespace Ouroboros.Modules.Identity.Application.Sessions;

internal sealed class LogoutAll(ISessionStore store, TimeProvider timeProvider) : ILogoutAll
{
    public async Task ExecuteAsync(LogoutAllRequest request, CancellationToken cancellationToken)
    {
        var userId = await store.FindUserIdAsync(request.UserId, cancellationToken);
        if (userId is null)
        {
            return;
        }

        // Trava todas as sessões do usuário antes de revogar, como em toda escrita nos tokens de uma
        // sessão; assim uma renovação em andamento não deixa um token novo para trás.
        var now = timeProvider.GetUtcNow();
        await using var transaction = await store.BeginTransactionAsync(cancellationToken);
        await store.LockUserSessionsAsync(userId.Value, cancellationToken);
        await store.RevokeUserTokensAsync(userId.Value, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
