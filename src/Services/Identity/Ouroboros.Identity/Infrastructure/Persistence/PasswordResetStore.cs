using Microsoft.EntityFrameworkCore;
using Ouroboros.Identity.Application.PasswordResets;
using Ouroboros.Identity.Domain.EmailTokens;
using Ouroboros.Identity.Domain.Users;

namespace Ouroboros.Identity.Infrastructure.Persistence;

internal sealed class PasswordResetStore(IdentityDbContext context) : IPasswordResetStore
{
    public Task<PasswordResetAccount?> FindCustomerAsync(string email, CancellationToken cancellationToken) =>
        context.Users.AsNoTracking().Where(user => user.Email == email && user.IsActive && user.Role == UserRole.Customer)
            .Select(user => new PasswordResetAccount(user.Id, user.Email, user.PasswordHash)).SingleOrDefaultAsync(cancellationToken);

    public Task<PasswordResetAccount?> FindActiveUserAsync(long userId, CancellationToken cancellationToken) =>
        context.Users.AsNoTracking().Where(user => user.Id == userId && user.IsActive)
            .Select(user => new PasswordResetAccount(user.Id, user.Email, user.PasswordHash)).SingleOrDefaultAsync(cancellationToken);

    public Task<PasswordResetToken?> FindPendingTokenAsync(byte[] hash, DateTimeOffset now, CancellationToken cancellationToken) =>
        context.EmailTokens.Where(token => token.TokenHash == hash && token.Purpose == EmailTokenPurpose.PasswordReset
                && token.UsedAt == null && token.RevokedAt == null && token.ExpiresAt > now)
            .Select(token => new PasswordResetToken(token.Id, token.UserId)).SingleOrDefaultAsync(cancellationToken);

    public async Task ReplacePendingTokenAsync(EmailToken token, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtext('identity:password-reset:' || {token.UserId}))", cancellationToken);
        await context.EmailTokens
            .Where(pending => pending.UserId == token.UserId && pending.Purpose == EmailTokenPurpose.PasswordReset
                && pending.UsedAt == null && pending.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(pending => pending.RevokedAt, now)
                .SetProperty(pending => pending.UpdatedAt, now), cancellationToken);
        context.EmailTokens.Add(token);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public Task<int> ConsumeTokenAsync(long tokenId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        RequireTransaction();
        return context.EmailTokens.Where(token => token.Id == tokenId && token.UsedAt == null
                && token.RevokedAt == null && token.ExpiresAt > now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(token => token.UsedAt, now)
                .SetProperty(token => token.UpdatedAt, now), cancellationToken);
    }

    public async Task ChangePasswordAsync(long userId, string passwordHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        RequireTransaction();
        await context.Users.Where(user => user.Id == userId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(user => user.PasswordHash, passwordHash)
                .SetProperty(user => user.FailedLoginCount, 0).SetProperty(user => user.LockedUntil, (DateTimeOffset?)null)
                .SetProperty(user => user.UpdatedAt, now), cancellationToken);
    }

    private void RequireTransaction()
    {
        if (context.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("A conclusão da redefinição exige a transação local de Identity.");
        }
    }
}
