using Microsoft.EntityFrameworkCore;
using Ouroboros.Identity.Application.Registrations;
using Ouroboros.Identity.Domain.EmailTokens;
using Ouroboros.Identity.Domain.Notifications;
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

    public async Task ReplacePendingTokenAsync(EmailToken token, EmailIntent intent, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (intent.ExpiresAt != token.ExpiresAt || intent.ProtectedEnvelope is null) { throw new InvalidOperationException("Intenção e token divergentes."); }
        intent.BindToToken(token.ExternalId);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtext('identity:registration:' || {token.Email}))", cancellationToken);
        var previous = await context.EmailTokens.Where(pending => pending.Email == token.Email && pending.Purpose == EmailTokenPurpose.Registration
            && pending.UsedAt == null && pending.RevokedAt == null).Select(pending => pending.ExternalId).ToArrayAsync(cancellationToken);
        await EmailIntentPersistence.CancelPendingAsync(context, previous, intent.SupersessionKey!, now, cancellationToken);
        await context.EmailTokens
            .Where(pending => pending.Email == token.Email && pending.Purpose == EmailTokenPurpose.Registration
                && pending.UsedAt == null && pending.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(pending => pending.RevokedAt, now)
                .SetProperty(pending => pending.UpdatedAt, now), cancellationToken);
        context.EmailTokens.Add(token);
        context.EmailIntents.Add(intent);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            context.Entry(token).State = EntityState.Detached;
            context.Entry(intent).State = EntityState.Detached;
            throw;
        }
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
