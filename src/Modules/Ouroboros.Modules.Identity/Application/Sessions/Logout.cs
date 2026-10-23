using Microsoft.EntityFrameworkCore;
using Ouroboros.Modules.Identity.Application.Security;
using Ouroboros.Modules.Identity.Contracts.Sessions;
using Ouroboros.Modules.Identity.Infrastructure.Persistence;

namespace Ouroboros.Modules.Identity.Application.Sessions;

internal sealed class Logout(IdentityDbContext context, TimeProvider timeProvider) : ILogout
{
    public async Task<LogoutResult> ExecuteAsync(LogoutRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return LogoutResult.InvalidToken;
        }

        var hash = RefreshTokens.Hash(request.RefreshToken);
        var token = await context.RefreshTokens
            .Where(token => token.TokenHash == hash)
            .Select(token => new { token.Id, token.SessionId })
            .SingleOrDefaultAsync(cancellationToken);
        if (token is null)
        {
            return LogoutResult.InvalidToken;
        }

        // Revoga só se ainda estiver ativo; já revogado ou expirado não muda nada (idempotente) e não é
        // tratado como reuso. A sessão é travada antes, como em toda escrita nos tokens de uma sessão.
        var now = timeProvider.GetUtcNow();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.LockSessionAsync(token.SessionId, cancellationToken);
        await context.RefreshTokens
            .Where(active => active.Id == token.Id && active.RevokedAt == null && active.ExpiresAt > now)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(active => active.RevokedAt, now)
                    .SetProperty(active => active.UpdatedAt, now),
                cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return LogoutResult.SessionEnded;
    }
}
