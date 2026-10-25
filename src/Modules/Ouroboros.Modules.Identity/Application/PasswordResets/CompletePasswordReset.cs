using Ouroboros.Modules.Identity.Application.Security;
using Ouroboros.Modules.Identity.Application.Sessions;
using Ouroboros.Modules.Identity.Contracts.PasswordResets;

namespace Ouroboros.Modules.Identity.Application.PasswordResets;

internal sealed class CompletePasswordReset(
    IPasswordResetStore store,
    ISessionStore sessions,
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
        var token = await store.FindPendingTokenAsync(hash, now, cancellationToken);
        var user = token?.UserId is { } userId
            ? await store.FindActiveUserAsync(userId, cancellationToken)
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
        await using var transaction = await sessions.BeginTransactionAsync(cancellationToken);
        await sessions.LockUserAsync(user.Id, cancellationToken);
        await sessions.LockUserSessionsAsync(user.Id, cancellationToken);

        // Estado lido sob o lock: a conta pode ter sido desativada e a senha pode ter mudado desde a leitura.
        var current = await sessions.ReadUserStateAsync(user.Id, cancellationToken);
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

        var consumed = await store.ConsumeTokenAsync(token.Id, now, cancellationToken);
        if (consumed == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return s_invalidToken;
        }

        await store.ChangePasswordAsync(user.Id, passwordHash, now, cancellationToken);
        await sessions.RevokeUserTokensAsync(user.Id, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new(CompletePasswordResetOutcome.Completed);
    }

    private bool IsCurrentPassword(string passwordHash, string password) =>
        passwordHasher.Verify(passwordHash, password) != PasswordVerificationOutcome.Failed;
}
