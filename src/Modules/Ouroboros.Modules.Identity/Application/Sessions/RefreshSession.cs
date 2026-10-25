using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ouroboros.Modules.Identity.Application.Security;
using Ouroboros.Modules.Identity.Contracts.Sessions;
using Ouroboros.Modules.Identity.Domain.Sessions;
using Ouroboros.Modules.Identity.Infrastructure.Persistence;

namespace Ouroboros.Modules.Identity.Application.Sessions;

internal sealed partial class RefreshSession(
    IdentityDbContext context,
    IAccessTokenIssuer accessTokenIssuer,
    SessionTokenOptions tokenOptions,
    TimeProvider timeProvider,
    ILogger<RefreshSession> logger) : IRefreshSession
{
    public async Task<RefreshSessionResult> ExecuteAsync(RefreshSessionRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return RefreshSessionResult.InvalidToken;
        }

        // O token é buscado pelo hash; o valor recebido nunca é gravado nem comparado em texto.
        var hash = OpaqueTokens.Hash(request.RefreshToken);
        var found = await (
            from token in context.RefreshTokens
            join session in context.Sessions on token.SessionId equals session.Id
            join user in context.Users on session.UserId equals user.Id
            where token.TokenHash == hash
            select new { Token = token, SessionId = session.ExternalId, User = user })
            .SingleOrDefaultAsync(cancellationToken);

        if (found is null)
        {
            return RefreshSessionResult.InvalidToken;
        }

        // Um token já revogado só pode estar com quem guardou uma cópia: o atacante ou o cliente legítimo
        // depois de o atacante renovar primeiro. Sem saber qual dos dois, a sessão inteira é encerrada.
        var reuse = new ReusedToken(found.Token.SessionId, found.SessionId, found.User.ExternalId);
        if (found.Token.RevokedAt is not null)
        {
            await RevokeSessionAsync(reuse, cancellationToken);
            return RefreshSessionResult.InvalidToken;
        }

        // Token apenas expirado não indica vazamento: não revoga a sessão.
        var now = timeProvider.GetUtcNow();
        if (!found.Token.IsActiveAt(now))
        {
            return RefreshSessionResult.InvalidToken;
        }

        // Usuário inativo ou de outro papel não consome o token.
        if (!found.User.IsActive || found.User.Role != LoginChannels.RoleOf(request.Channel))
        {
            return RefreshSessionResult.InvalidToken;
        }

        // Revogar o antigo e gravar o novo na mesma transação, com a sessão travada (veja
        // IdentityDbContext.LockSessionAsync).
        // O UPDATE condicional garante que só uma renovação simultânea com o mesmo token vence.
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.LockSessionAsync(found.Token.SessionId, cancellationToken);
        var revoked = await context.RefreshTokens
            .Where(token => token.Id == found.Token.Id && token.RevokedAt == null && token.ExpiresAt > now)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(token => token.RevokedAt, now)
                    .SetProperty(token => token.UpdatedAt, now),
                cancellationToken);
        if (revoked == 0)
        {
            // Outra renovação revogou o token primeiro (reuso) ou ele acabou de expirar. A revogação da
            // sessão roda depois do rollback, numa transação própria, para não ser desfeita.
            await transaction.RollbackAsync(cancellationToken);
            if (await context.RefreshTokens.AnyAsync(token => token.Id == found.Token.Id && token.RevokedAt != null, cancellationToken))
            {
                await RevokeSessionAsync(reuse, cancellationToken);
            }

            return RefreshSessionResult.InvalidToken;
        }

        var (refreshToken, refreshTokenHash) = OpaqueTokens.Generate();
        var refreshTokenExpiresAt = now.Add(tokenOptions.RefreshTokenLifetime);
        context.RefreshTokens.Add(RefreshToken.Issue(found.Token.SessionId, refreshTokenHash, refreshTokenExpiresAt));
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // O papel vem do banco, então o access token novo reflete o papel atual do usuário.
        var accessToken = accessTokenIssuer.Issue(found.User.ExternalId, found.User.Role, found.SessionId);
        return new RefreshSessionResult(new SessionTokens(accessToken.Token, accessToken.ExpiresAt, refreshToken, refreshTokenExpiresAt));
    }

    // Revoga todos os tokens ativos da sessão, numa transação própria e com a sessão travada. As outras
    // sessões do usuário não são afetadas.
    private async Task RevokeSessionAsync(ReusedToken reuse, CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.LockSessionAsync(reuse.SessionDatabaseId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var revoked = await context.RefreshTokens
            .Where(token => token.SessionId == reuse.SessionDatabaseId && token.RevokedAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(token => token.RevokedAt, now)
                    .SetProperty(token => token.UpdatedAt, now),
                cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // Só registra quando a sessão ainda tinha tokens ativos; reapresentações seguintes não repetem o aviso.
        if (revoked > 0)
        {
            LogReuseDetected(reuse.UserId, reuse.SessionId);
        }
    }

    // Só os ids externos do usuário e da sessão: nunca o token.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Reuso de refresh token detectado; sessão {SessionId} do usuário {UserId} revogada.")]
    private partial void LogReuseDetected(Guid userId, Guid sessionId);

    private sealed record ReusedToken(long SessionDatabaseId, Guid SessionId, Guid UserId);
}
