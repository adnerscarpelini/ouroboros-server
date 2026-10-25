using Ouroboros.BuildingBlocks.Domain;

namespace Ouroboros.Identity.Domain.Sessions;

// Refresh token de uma sessão. O valor entregue ao cliente nunca é gravado: só o hash SHA-256.
internal sealed class RefreshToken : Entity
{
    public const int HashLength = 32;

    private RefreshToken(byte[] tokenHash, DateTimeOffset expiresAt)
    {
        TokenHash = tokenHash;
        ExpiresAt = expiresAt;
    }

    public long SessionId { get; private set; }

    public byte[] TokenHash { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    // Momento em que o token deixou de valer (rotação, logout ou reuso); null enquanto ativo.
    public DateTimeOffset? RevokedAt { get; private set; }

    public bool IsActiveAt(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;

    // Token seguinte de uma sessão já gravada, emitido na renovação.
    public static RefreshToken Issue(long sessionId, byte[] tokenHash, DateTimeOffset expiresAt)
    {
        var token = Create(tokenHash, expiresAt);
        token.SessionId = sessionId;
        return token;
    }

    public static RefreshToken Create(byte[] tokenHash, DateTimeOffset expiresAt)
    {
        if (tokenHash.Length != HashLength)
        {
            throw new ArgumentException($"O hash do refresh token deve ter {HashLength} bytes.", nameof(tokenHash));
        }

        return new RefreshToken(tokenHash, expiresAt);
    }
}
