using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ouroboros.Identity.Application.Sessions;
using Ouroboros.Identity.Domain.Sessions;
using Ouroboros.Identity.Domain.Users;

namespace Ouroboros.Identity.Infrastructure.Persistence;

internal sealed class SessionStore(IdentityDbContext context) : ISessionStore
{
    public Task<User?> FindUserAsync(Guid userId, CancellationToken cancellationToken) =>
        context.Users.AsNoTracking().SingleOrDefaultAsync(user => user.ExternalId == userId, cancellationToken);

    public Task<bool> OwnsSessionAsync(long userId, Guid sessionId, CancellationToken cancellationToken) =>
        context.Sessions.AnyAsync(session => session.UserId == userId && session.ExternalId == sessionId, cancellationToken);

    public async Task ChangePasswordAndRevokeOtherTokensAsync(long userId, Guid sessionId, string hash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (context.Database.CurrentTransaction is null) { throw new InvalidOperationException("A troca exige a transação local de Identity."); }
        await context.Users.Where(user => user.Id == userId).ExecuteUpdateAsync(setters => setters
            .SetProperty(user => user.PasswordHash, hash).SetProperty(user => user.FailedLoginCount, 0)
            .SetProperty(user => user.LockedUntil, (DateTimeOffset?)null).SetProperty(user => user.UpdatedAt, now), cancellationToken);
        await context.RefreshTokens.Where(token => token.RevokedAt == null && context.Sessions.Any(session =>
                session.Id == token.SessionId && session.UserId == userId && session.ExternalId != sessionId))
            .ExecuteUpdateAsync(setters => setters.SetProperty(token => token.RevokedAt, now)
                .SetProperty(token => token.UpdatedAt, now), cancellationToken);
    }

    public Task<User?> FindUserAsync(string normalizedEmail, CancellationToken cancellationToken) =>
        context.Users.SingleOrDefaultAsync(user => user.Email == normalizedEmail, cancellationToken);

    public Task<long?> FindUserIdAsync(Guid userId, CancellationToken cancellationToken) =>
        context.Users.Where(user => user.ExternalId == userId).Select(user => (long?)user.Id).SingleOrDefaultAsync(cancellationToken);

    public Task<StoredRefreshToken?> FindRefreshTokenAsync(byte[] hash, CancellationToken cancellationToken) =>
        (from token in context.RefreshTokens
         join session in context.Sessions on token.SessionId equals session.Id
         join user in context.Users on session.UserId equals user.Id
         where token.TokenHash == hash
         select new StoredRefreshToken(token, session.ExternalId, user))
        .SingleOrDefaultAsync(cancellationToken);

    public Task<RefreshTokenReference?> FindRefreshTokenReferenceAsync(byte[] hash, CancellationToken cancellationToken) =>
        context.RefreshTokens.Where(token => token.TokenHash == hash)
            .Select(token => new RefreshTokenReference(token.Id, token.SessionId)).SingleOrDefaultAsync(cancellationToken);

    public async Task<bool> RegisterFailureAsync(long userId, int maxFailedAttempts, DateTimeOffset lockedUntil, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Um único UPDATE evita perder incrementos concorrentes; não compor LINQ sobre o RETURNING.
        var results = await context.Database.SqlQuery<bool>($"""
            UPDATE identity.users
            SET failed_login_count = CASE WHEN failed_login_count + 1 >= {maxFailedAttempts} THEN 0 ELSE failed_login_count + 1 END,
                locked_until = CASE WHEN failed_login_count + 1 >= {maxFailedAttempts} THEN {lockedUntil} ELSE locked_until END,
                updated_at = {now}
            WHERE id = {userId}
            RETURNING failed_login_count = 0 AS "Value"
            """).ToListAsync(cancellationToken);
        return results is [true];
    }

    public async Task<ISessionTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        new SessionTransaction(await context.Database.BeginTransactionAsync(cancellationToken));

    public Task LockUserAsync(long userId, CancellationToken cancellationToken) =>
        context.LockUserAsync(userId, cancellationToken);

    // A consulta não pode devolver a instância rastreada antes do lock: precisamos do estado vigente.
    public Task<SessionUserState> ReadUserStateAsync(long userId, CancellationToken cancellationToken) =>
        context.Users.AsNoTracking().Where(user => user.Id == userId)
            .Select(user => new SessionUserState(user.PasswordHash, user.IsActive, user.LockedUntil)).SingleAsync(cancellationToken);

    public Task LockSessionAsync(long sessionId, CancellationToken cancellationToken) =>
        context.LockSessionAsync(sessionId, cancellationToken);

    public Task LockUserSessionsAsync(long userId, CancellationToken cancellationToken) =>
        context.LockUserSessionsAsync(userId, cancellationToken);

    public void AddSession(Session session) => context.Sessions.Add(session);

    public void AddRefreshToken(RefreshToken token) => context.RefreshTokens.Add(token);

    public async Task SaveChangesAsync(CancellationToken cancellationToken) =>
        await context.SaveChangesAsync(cancellationToken);

    public Task<int> RevokeTokenAsync(long tokenId, DateTimeOffset now, CancellationToken cancellationToken) =>
        context.RefreshTokens.Where(token => token.Id == tokenId && token.RevokedAt == null && token.ExpiresAt > now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(token => token.RevokedAt, now).SetProperty(token => token.UpdatedAt, now), cancellationToken);

    public Task<bool> IsTokenRevokedAsync(long tokenId, CancellationToken cancellationToken) =>
        context.RefreshTokens.AnyAsync(token => token.Id == tokenId && token.RevokedAt != null, cancellationToken);

    public Task<int> RevokeSessionTokensAsync(long sessionId, DateTimeOffset now, CancellationToken cancellationToken) =>
        context.RefreshTokens.Where(token => token.SessionId == sessionId && token.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(token => token.RevokedAt, now).SetProperty(token => token.UpdatedAt, now), cancellationToken);

    public async Task RevokeUserTokensAsync(long userId, DateTimeOffset now, CancellationToken cancellationToken) =>
        await context.RefreshTokens.Where(token => token.RevokedAt == null && token.ExpiresAt > now
                && context.Sessions.Any(session => session.Id == token.SessionId && session.UserId == userId))
            .ExecuteUpdateAsync(setters => setters.SetProperty(token => token.RevokedAt, now).SetProperty(token => token.UpdatedAt, now), cancellationToken);

    private sealed class SessionTransaction(IDbContextTransaction transaction) : ISessionTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken) => transaction.CommitAsync(cancellationToken);

        public Task RollbackAsync(CancellationToken cancellationToken) => transaction.RollbackAsync(cancellationToken);

        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }
}
