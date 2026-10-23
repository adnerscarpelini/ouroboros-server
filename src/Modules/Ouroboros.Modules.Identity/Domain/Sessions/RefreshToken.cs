using Ouroboros.BuildingBlocks.Domain;

namespace Ouroboros.Modules.Identity.Domain.Sessions;

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

    public static RefreshToken Create(byte[] tokenHash, DateTimeOffset expiresAt)
    {
        if (tokenHash.Length != HashLength)
        {
            throw new ArgumentException($"O hash do refresh token deve ter {HashLength} bytes.", nameof(tokenHash));
        }

        return new RefreshToken(tokenHash, expiresAt);
    }
}
