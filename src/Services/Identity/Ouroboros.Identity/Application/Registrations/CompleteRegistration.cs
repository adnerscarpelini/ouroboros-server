using Ouroboros.Identity.Application.Security;
using Ouroboros.Identity.Contracts.Registrations;
using Ouroboros.Identity.Domain.Users;

namespace Ouroboros.Identity.Application.Registrations;

internal sealed class CompleteRegistration(
    IRegistrationStore store,
    IPasswordHasher passwordHasher,
    TimeProvider timeProvider) : ICompleteRegistration
{
    private static readonly CompleteRegistrationResult s_invalidToken = new(CompleteRegistrationOutcome.InvalidToken);

    public async Task<CompleteRegistrationResult> ExecuteAsync(CompleteRegistrationRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Token))
        {
            return s_invalidToken;
        }

        var hash = OpaqueTokens.Hash(request.Token);
        var now = timeProvider.GetUtcNow();
        var token = await store.FindPendingTokenAsync(hash, now, cancellationToken);
        if (token is null)
        {
            return s_invalidToken;
        }

        // Erro de senha não consome o token.
        var password = request.Password ?? "";
        if (PasswordPolicy.Validate(password, token.Email) is { } rule)
        {
            return new(CompleteRegistrationOutcome.PasswordRejected, rule);
        }

        // O hash é caro: é calculado antes de abrir a transação.
        var passwordHash = passwordHasher.Hash(password);

        var completed = await store.CompleteAsync(
            token.Id, User.Create(token.Email, passwordHash, UserRole.Customer), now, cancellationToken);
        return completed ? new(CompleteRegistrationOutcome.Completed) : s_invalidToken;
    }
}
