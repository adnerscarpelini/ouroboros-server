using Ouroboros.Modules.Identity.Application.Security;
using Ouroboros.Modules.Identity.Contracts.Sessions;

namespace Ouroboros.Modules.Identity.Application.Sessions;

internal sealed class Logout(ISessionStore store, TimeProvider timeProvider) : ILogout
{
    public async Task<LogoutResult> ExecuteAsync(LogoutRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return LogoutResult.InvalidToken;
        }

        var hash = OpaqueTokens.Hash(request.RefreshToken);
        var token = await store.FindRefreshTokenReferenceAsync(hash, cancellationToken);
        if (token is null)
        {
            return LogoutResult.InvalidToken;
        }

        // Revoga só se ainda estiver ativo; já revogado ou expirado não muda nada (idempotente) e não é
        // tratado como reuso. A sessão é travada antes, como em toda escrita nos tokens de uma sessão.
        var now = timeProvider.GetUtcNow();
        await using var transaction = await store.BeginTransactionAsync(cancellationToken);
        await store.LockSessionAsync(token.SessionId, cancellationToken);
        await store.RevokeTokenAsync(token.Id, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return LogoutResult.SessionEnded;
    }
}
