using Microsoft.EntityFrameworkCore;
using Ouroboros.Modules.Identity.Application.Security;
using Ouroboros.Modules.Identity.Contracts.Registrations;
using Ouroboros.Modules.Identity.Domain.EmailTokens;
using Ouroboros.Modules.Identity.Domain.Users;
using Ouroboros.Modules.Identity.Infrastructure.Persistence;

namespace Ouroboros.Modules.Identity.Application.Registrations;

internal sealed class CompleteRegistration(
    IdentityDbContext context,
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
        var token = await context.EmailTokens
            .AsNoTracking()
            .Where(token => token.TokenHash == hash
                && token.Purpose == EmailTokenPurpose.Registration
                && token.UsedAt == null
                && token.RevokedAt == null
                && token.ExpiresAt > now)
            .Select(token => new { token.Id, token.Email })
            .SingleOrDefaultAsync(cancellationToken);
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

        // Consumir o token e criar o usuário na mesma transação. O UPDATE condicional garante que só uma
        // conclusão simultânea com o mesmo token vence.
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
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
            return s_invalidToken;
        }

        // O e-mail pode ter passado a pertencer a outra conta depois da solicitação. O índice único de
        // e-mail também barra uma criação simultânea; em ambos os casos nada é gravado.
        if (await context.Users.AnyAsync(user => user.Email == token.Email, cancellationToken))
        {
            return s_invalidToken;
        }

        context.Users.Add(User.Create(token.Email, passwordHash, UserRole.Customer));
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (PersistenceErrors.IsUniqueViolation(exception))
        {
            return s_invalidToken;
        }

        await transaction.CommitAsync(cancellationToken);
        return new(CompleteRegistrationOutcome.Completed);
    }
}
