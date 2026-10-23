using Microsoft.EntityFrameworkCore;
using Ouroboros.Modules.Identity.Application.Security;
using Ouroboros.Modules.Identity.Contracts.PasswordResets;
using Ouroboros.Modules.Identity.Domain.EmailTokens;
using Ouroboros.Modules.Identity.Infrastructure.Persistence;

namespace Ouroboros.Modules.Identity.Application.PasswordResets;

internal sealed class CompletePasswordReset(
    IdentityDbContext context,
    IPasswordHasher passwordHasher,
    TimeProvider timeProvider) : ICompletePasswordReset
{
    private static readonly CompletePasswordResetResult s_invalidToken = new(CompletePasswordResetOutcome.InvalidToken);

    private static readonly CompletePasswordResetResult s_samePassword =
        new(CompletePasswordResetOutcome.PasswordRejected, "A nova senha não pode ser igual à atual.");

    public async Task<CompletePasswordResetResult> ExecuteAsync(CompletePasswordResetRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Token))
        {
            return s_invalidToken;
        }

        var hash = OpaqueTokens.Hash(request.Token);
        var now = timeProvider.GetUtcNow();
        var token = await context.EmailTokens
            .Where(token => token.TokenHash == hash
                && token.Purpose == EmailTokenPurpose.PasswordReset
                && token.UsedAt == null
                && token.RevokedAt == null
                && token.ExpiresAt > now)
            .Select(token => new { token.Id, token.UserId })
            .SingleOrDefaultAsync(cancellationToken);
        var user = token?.UserId is { } userId
            ? await context.Users
                .AsNoTracking()
                .Where(user => user.Id == userId && user.IsActive)
                .Select(user => new { user.Id, user.Email, user.PasswordHash })
                .SingleOrDefaultAsync(cancellationToken)
            : null;
        if (token is null || user is null)
        {
            return s_invalidToken;
        }

        // Erros de senha não consomem o token.
        var password = request.Password ?? "";
        if (PasswordPolicy.Validate(password, user.Email) is { } rule)
        {
            return new(CompletePasswordResetOutcome.PasswordRejected, rule);
        }

        // Verificação e hash são caros: rodam antes de abrir a transação, para não segurar o lock do usuário.
        if (IsCurrentPassword(user.PasswordHash, password))
        {
            return s_samePassword;
        }

        var passwordHash = passwordHasher.Hash(password);

        // Numa única transação: consumir o token, trocar a senha, remover o bloqueio e revogar todos os
        // refresh tokens. O usuário é travado primeiro (serializa com a abertura de sessão no login) e depois
        // as sessões dele (serializa com renovações em andamento), na mesma ordem dos outros fluxos.
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.LockUserAsync(user.Id, cancellationToken);
        await context.LockUserSessionsAsync(user.Id, cancellationToken);

        // Estado lido sob o lock: a conta pode ter sido desativada e a senha pode ter mudado desde a leitura.
        var current = await context.Users
            .AsNoTracking()
            .Where(stored => stored.Id == user.Id)
            .Select(stored => new { stored.PasswordHash, stored.IsActive })
            .SingleAsync(cancellationToken);
        if (!current.IsActive)
        {
            await transaction.RollbackAsync(cancellationToken);
            return s_invalidToken;
        }

        // Só repete a verificação se a senha mudou desde a leitura (caso raro).
        if (current.PasswordHash != user.PasswordHash && IsCurrentPassword(current.PasswordHash, password))
        {
            await transaction.RollbackAsync(cancellationToken);
            return s_samePassword;
        }

        var consumed = await context.EmailTokens
            .Where(pending => pending.Id == token.Id
                && pending.UsedAt == null
                && pending.RevokedAt == null
                && pending.ExpiresAt > now)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(pending => pending.UsedAt, now)
                    .SetProperty(pending => pending.UpdatedAt, now),
                cancellationToken);
        if (consumed == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return s_invalidToken;
        }

        // UPDATE explícito, sem depender do rastreamento do EF: grava as três colunas mesmo que um bloqueio
        // tenha entrado depois da leitura.
        await context.Users
            .Where(stored => stored.Id == user.Id)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(stored => stored.PasswordHash, passwordHash)
                    .SetProperty(stored => stored.FailedLoginCount, 0)
                    .SetProperty(stored => stored.LockedUntil, (DateTimeOffset?)null)
                    .SetProperty(stored => stored.UpdatedAt, now),
                cancellationToken);
        await context.RefreshTokens
            .Where(refreshToken => refreshToken.RevokedAt == null
                && refreshToken.ExpiresAt > now
                && context.Sessions.Any(session => session.Id == refreshToken.SessionId && session.UserId == user.Id))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(refreshToken => refreshToken.RevokedAt, now)
                    .SetProperty(refreshToken => refreshToken.UpdatedAt, now),
                cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new(CompletePasswordResetOutcome.Completed);
    }

    private bool IsCurrentPassword(string passwordHash, string password) =>
        passwordHasher.Verify(passwordHash, password) != PasswordVerificationOutcome.Failed;
}
