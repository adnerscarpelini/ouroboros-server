using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ouroboros.Modules.Identity.Application.Security;
using Ouroboros.Modules.Identity.Contracts.Sessions;
using Ouroboros.Modules.Identity.Domain.Sessions;
using Ouroboros.Modules.Identity.Infrastructure.Persistence;
using Ouroboros.Modules.Identity.Infrastructure.Security;

namespace Ouroboros.Modules.Identity.Application.Sessions;

internal sealed class RefreshSession(
    IdentityDbContext context,
    AccessTokenIssuer accessTokenIssuer,
    IOptions<JwtOptions> jwtOptions,
    TimeProvider timeProvider) : IRefreshSession
{
    public async Task<RefreshSessionResult> ExecuteAsync(RefreshSessionRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return RefreshSessionResult.InvalidToken;
        }

        // O token é buscado pelo hash; o valor recebido nunca é gravado nem comparado em texto.
        var hash = RefreshTokens.Hash(request.RefreshToken);
        var found = await (
            from token in context.RefreshTokens
            join session in context.Sessions on token.SessionId equals session.Id
            join user in context.Users on session.UserId equals user.Id
            where token.TokenHash == hash
            select new { Token = token, SessionId = session.ExternalId, User = user })
            .SingleOrDefaultAsync(cancellationToken);

        var now = timeProvider.GetUtcNow();
        if (found is null || !found.Token.IsActiveAt(now))
        {
            return RefreshSessionResult.InvalidToken;
        }

        // Usuário inativo ou de outro papel não consome o token.
        if (!found.User.IsActive || found.User.Role != LoginChannels.RoleOf(request.Channel))
        {
            return RefreshSessionResult.InvalidToken;
        }

        // Revogar o antigo e gravar o novo na mesma transação. O UPDATE condicional garante que só uma
        // renovação simultânea com o mesmo token vence; as outras não revogam nada e recebem 401.
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var revoked = await context.RefreshTokens
            .Where(token => token.Id == found.Token.Id && token.RevokedAt == null && token.ExpiresAt > now)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(token => token.RevokedAt, now)
                    .SetProperty(token => token.UpdatedAt, now),
                cancellationToken);
        if (revoked == 0)
        {
            return RefreshSessionResult.InvalidToken;
        }

        var (refreshToken, refreshTokenHash) = RefreshTokens.Generate();
        var refreshTokenExpiresAt = now.Add(jwtOptions.Value.RefreshTokenLifetime);
        context.RefreshTokens.Add(RefreshToken.Issue(found.Token.SessionId, refreshTokenHash, refreshTokenExpiresAt));
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // O papel vem do banco, então o access token novo reflete o papel atual do usuário.
        var accessToken = accessTokenIssuer.Issue(found.User, found.SessionId);
        return new RefreshSessionResult(new SessionTokens(accessToken.Token, accessToken.ExpiresAt, refreshToken, refreshTokenExpiresAt));
    }
}
