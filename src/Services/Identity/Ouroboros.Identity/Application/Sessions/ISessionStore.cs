using Ouroboros.Identity.Domain.Sessions;
using Ouroboros.Identity.Domain.Users;

namespace Ouroboros.Identity.Application.Sessions;

// Porta interna e scoped. Locks, escritas e SaveChanges participam da mesma transação local.
// Ordem: usuário -> sessões (id crescente) -> tokens. Quem trava só sessão não trava usuário depois.
internal interface ISessionStore
{
    Task<User?> FindUserAsync(string normalizedEmail, CancellationToken cancellationToken);
    Task<User?> FindUserAsync(Guid userId, CancellationToken cancellationToken);
    Task<bool> OwnsSessionAsync(long userId, Guid sessionId, CancellationToken cancellationToken);
    Task ChangePasswordAndRevokeOtherTokensAsync(long userId, Guid sessionId, string hash, DateTimeOffset now, CancellationToken cancellationToken);
    Task<long?> FindUserIdAsync(Guid userId, CancellationToken cancellationToken);
    Task<StoredRefreshToken?> FindRefreshTokenAsync(byte[] hash, CancellationToken cancellationToken);
    Task<RefreshTokenReference?> FindRefreshTokenReferenceAsync(byte[] hash, CancellationToken cancellationToken);
    Task<bool> RegisterFailureAsync(long userId, int maxFailedAttempts, DateTimeOffset lockedUntil, DateTimeOffset now, CancellationToken cancellationToken);
    Task<ISessionTransaction> BeginTransactionAsync(CancellationToken cancellationToken);
    Task LockUserAsync(long userId, CancellationToken cancellationToken);
    Task<SessionUserState> ReadUserStateAsync(long userId, CancellationToken cancellationToken);
    Task LockSessionAsync(long sessionId, CancellationToken cancellationToken);
    Task LockUserSessionsAsync(long userId, CancellationToken cancellationToken);
    void AddSession(Session session);
    void AddRefreshToken(RefreshToken token);
    Task SaveChangesAsync(CancellationToken cancellationToken);
    Task<int> RevokeTokenAsync(long tokenId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> IsTokenRevokedAsync(long tokenId, CancellationToken cancellationToken);
    Task<int> RevokeSessionTokensAsync(long sessionId, DateTimeOffset now, CancellationToken cancellationToken);
    Task RevokeUserTokensAsync(long userId, DateTimeOffset now, CancellationToken cancellationToken);
}

// O descarte sem commit desfaz a unidade de trabalho inteira; rollback libera a transação antes do reuso.
internal interface ISessionTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
    Task RollbackAsync(CancellationToken cancellationToken);
}

internal sealed record SessionUserState(string PasswordHash, bool IsActive, DateTimeOffset? LockedUntil);
internal sealed record RefreshTokenReference(long Id, long SessionId);
internal sealed record StoredRefreshToken(RefreshToken Token, Guid SessionId, User User);
