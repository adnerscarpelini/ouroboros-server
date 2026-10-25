using Ouroboros.BuildingBlocks.Domain;

namespace Ouroboros.Identity.Domain.Sessions;

// Sessão aberta por um login. O ExternalId vai no claim sid do access token. Cada login abre uma
// sessão nova, sem encerrar as anteriores.
internal sealed class Session : Entity
{
    private readonly List<RefreshToken> _refreshTokens = [];

    private Session(long userId) => UserId = userId;

    public long UserId { get; private set; }

    public IReadOnlyCollection<RefreshToken> RefreshTokens => _refreshTokens;

    // Abre a sessão já com o primeiro refresh token, do qual só o hash é guardado.
    public static Session Open(long userId, byte[] refreshTokenHash, DateTimeOffset refreshTokenExpiresAt)
    {
        var session = new Session(userId);
        session._refreshTokens.Add(RefreshToken.Create(refreshTokenHash, refreshTokenExpiresAt));
        return session;
    }
}
