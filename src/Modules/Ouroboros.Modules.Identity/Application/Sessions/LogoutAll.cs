using Microsoft.EntityFrameworkCore;
using Ouroboros.Modules.Identity.Contracts.Sessions;
using Ouroboros.Modules.Identity.Infrastructure.Persistence;

namespace Ouroboros.Modules.Identity.Application.Sessions;

internal sealed class LogoutAll(IdentityDbContext context, TimeProvider timeProvider) : ILogoutAll
{
    public async Task ExecuteAsync(LogoutAllRequest request, CancellationToken cancellationToken)
    {
        var userId = await context.Users
            .Where(user => user.ExternalId == request.UserId)
            .Select(user => (long?)user.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (userId is null)
        {
            return;
        }

        // Trava todas as sessões do usuário antes de revogar, como em toda escrita nos tokens de uma
        // sessão; assim uma renovação em andamento não deixa um token novo para trás.
        var now = timeProvider.GetUtcNow();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.LockUserSessionsAsync(userId.Value, cancellationToken);
        await context.RefreshTokens
            .Where(token => token.RevokedAt == null
                && token.ExpiresAt > now
                && context.Sessions.Any(session => session.Id == token.SessionId && session.UserId == userId))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(token => token.RevokedAt, now)
                    .SetProperty(token => token.UpdatedAt, now),
                cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
