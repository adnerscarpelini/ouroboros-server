using Ouroboros.Identity.Application.Security;
using Ouroboros.Identity.Application.Sessions;
using Ouroboros.Identity.Contracts.Users;

namespace Ouroboros.Identity.Application.Users;

internal sealed class ChangePassword(ISessionStore store, IPasswordHasher hasher, AccountLockout lockout, TimeProvider clock) : IChangePassword
{
    public async Task<ChangePasswordResult> ExecuteAsync(ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        var policy = await lockout.ReadPolicyAsync(cancellationToken);
        var user = await store.FindUserAsync(request.UserId, cancellationToken);
        var now = clock.GetUtcNow();
        if (user is null || !user.IsActive || user.IsLockedAt(now) || user.Role != LoginChannels.RoleOf(request.Channel))
        {
            return new(ChangePasswordOutcome.InvalidCredentials);
        }

        var verifiedHash = user.PasswordHash;
        if (hasher.Verify(verifiedHash, request.CurrentPassword ?? "") == PasswordVerificationOutcome.Failed)
        {
            // Fora da transação da troca: a falha de credencial deve sobreviver à recusa da operação.
            await lockout.RegisterFailureAsync(user, policy, now, cancellationToken);
            return new(ChangePasswordOutcome.InvalidCredentials);
        }

        var password = request.NewPassword ?? "";
        if (PasswordPolicy.Validate(password, user.Email) is { } rule)
        {
            return new(ChangePasswordOutcome.PasswordRejected, rule);
        }

        if (hasher.Verify(verifiedHash, password) != PasswordVerificationOutcome.Failed)
        {
            return new(ChangePasswordOutcome.PasswordRejected, "A nova senha não pode ser igual à atual.");
        }

        var hash = hasher.Hash(password);
        await using var transaction = await store.BeginTransactionAsync(cancellationToken);
        await store.LockUserAsync(user.Id, cancellationToken);
        var current = await store.ReadUserStateAsync(user.Id, cancellationToken);
        if (!current.IsActive || current.LockedUntil > clock.GetUtcNow() || current.PasswordHash != verifiedHash)
        {
            return new(ChangePasswordOutcome.InvalidCredentials);
        }

        await store.LockUserSessionsAsync(user.Id, cancellationToken);
        if (!await store.OwnsSessionAsync(user.Id, request.SessionId, cancellationToken))
        {
            return new(ChangePasswordOutcome.InvalidCredentials);
        }

        await store.ChangePasswordAndRevokeOtherTokensAsync(user.Id, request.SessionId, hash, clock.GetUtcNow(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(ChangePasswordOutcome.Changed);
    }
}
