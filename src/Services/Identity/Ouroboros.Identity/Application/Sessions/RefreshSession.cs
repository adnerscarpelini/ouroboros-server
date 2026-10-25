using Microsoft.Extensions.Logging;
using Ouroboros.Identity.Application.Security;
using Ouroboros.Identity.Contracts.Sessions;
using Ouroboros.Identity.Domain.Sessions;

namespace Ouroboros.Identity.Application.Sessions;

internal sealed partial class RefreshSession(
    ISessionStore store,
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
        var found = await store.FindRefreshTokenAsync(hash, cancellationToken);

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
        // ISessionStore.LockSessionAsync).
        // O UPDATE condicional garante que só uma renovação simultânea com o mesmo token vence.
        await using var transaction = await store.BeginTransactionAsync(cancellationToken);
        await store.LockSessionAsync(found.Token.SessionId, cancellationToken);
        var revoked = await store.RevokeTokenAsync(found.Token.Id, now, cancellationToken);
        if (revoked == 0)
        {
            // Outra renovação revogou o token primeiro (reuso) ou ele acabou de expirar. A revogação da
            // sessão roda depois do rollback, numa transação própria, para não ser desfeita.
            await transaction.RollbackAsync(cancellationToken);
            if (await store.IsTokenRevokedAsync(found.Token.Id, cancellationToken))
            {
                await RevokeSessionAsync(reuse, cancellationToken);
            }

            return RefreshSessionResult.InvalidToken;
        }

        var (refreshToken, refreshTokenHash) = OpaqueTokens.Generate();
        var refreshTokenExpiresAt = now.Add(tokenOptions.RefreshTokenLifetime);
        store.AddRefreshToken(RefreshToken.Issue(found.Token.SessionId, refreshTokenHash, refreshTokenExpiresAt));
        await store.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // O papel vem do banco, então o access token novo reflete o papel atual do usuário.
        var accessToken = accessTokenIssuer.Issue(found.User.ExternalId, found.User.Role, found.SessionId);
        return new RefreshSessionResult(new SessionTokens(accessToken.Token, accessToken.ExpiresAt, refreshToken, refreshTokenExpiresAt));
    }

    // Revoga todos os tokens ativos da sessão, numa transação própria e com a sessão travada. As outras
    // sessões do usuário não são afetadas.
    private async Task RevokeSessionAsync(ReusedToken reuse, CancellationToken cancellationToken)
    {
        await using var transaction = await store.BeginTransactionAsync(cancellationToken);
        await store.LockSessionAsync(reuse.SessionDatabaseId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var revoked = await store.RevokeSessionTokensAsync(reuse.SessionDatabaseId, now, cancellationToken);
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
