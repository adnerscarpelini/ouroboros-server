using Microsoft.EntityFrameworkCore;
using Ouroboros.Identity.Application.Registrations;
using Ouroboros.Identity.Domain.EmailTokens;
using Ouroboros.Identity.Domain.Users;

namespace Ouroboros.Identity.Infrastructure.Persistence;

internal sealed class RegistrationStore(IdentityDbContext context) : IRegistrationStore
{
    public Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken) =>
        context.Users.AnyAsync(user => user.Email == email, cancellationToken);

    public Task<RegistrationToken?> FindPendingTokenAsync(byte[] hash, DateTimeOffset now, CancellationToken cancellationToken) =>
        context.EmailTokens.AsNoTracking()
            .Where(token => token.TokenHash == hash && token.Purpose == EmailTokenPurpose.Registration
                && token.UsedAt == null && token.RevokedAt == null && token.ExpiresAt > now)
            .Select(token => new RegistrationToken(token.Id, token.Email)).SingleOrDefaultAsync(cancellationToken);

    public async Task ReplacePendingTokenAsync(EmailToken token, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtext('identity:registration:' || {token.Email}))", cancellationToken);
        await context.EmailTokens
            .Where(pending => pending.Email == token.Email && pending.Purpose == EmailTokenPurpose.Registration
                && pending.UsedAt == null && pending.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(pending => pending.RevokedAt, now)
                .SetProperty(pending => pending.UpdatedAt, now), cancellationToken);
        context.EmailTokens.Add(token);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> CompleteAsync(long tokenId, User user, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Consumo condicional e unicidade da conta pertencem ao mesmo commit. Toda saída sem commit desfaz tudo.
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var consumed = await context.EmailTokens
            .Where(pending => pending.Id == tokenId && pending.UsedAt == null
                && pending.RevokedAt == null && pending.ExpiresAt > now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(pending => pending.UsedAt, now)
                .SetProperty(pending => pending.UpdatedAt, now), cancellationToken);
        if (consumed == 0 || await EmailExistsAsync(user.Email, cancellationToken))
        {
            return false;
        }

        context.Users.Add(user);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (PersistenceErrors.IsUniqueViolation(exception))
        {
            context.Entry(user).State = EntityState.Detached;
            return false;
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
