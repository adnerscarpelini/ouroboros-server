using Ouroboros.BuildingBlocks.Domain;

namespace Ouroboros.Identity.Domain.EmailTokens;

// Token de uso único enviado por e-mail (link de cadastro ou de redefinição de senha). O valor
// entregue no link nunca é gravado: só o hash SHA-256.
internal sealed class EmailToken : Entity
{
    public const int HashLength = 32;

    private EmailToken(EmailTokenPurpose purpose, string email, long? userId, byte[] tokenHash, DateTimeOffset expiresAt)
    {
        Purpose = purpose;
        Email = email;
        UserId = userId;
        TokenHash = tokenHash;
        ExpiresAt = expiresAt;
    }

    public EmailTokenPurpose Purpose { get; private set; }

    // E-mail normalizado para onde o link foi enviado.
    public string Email { get; private set; }

    // Dono do token quando ele se refere a uma conta existente (redefinição); null no cadastro.
    public long? UserId { get; private set; }

    public byte[] TokenHash { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    // Momento em que o token foi usado com sucesso; null enquanto pendente.
    public DateTimeOffset? UsedAt { get; private set; }

    // Momento em que o token foi invalidado por uma solicitação mais nova; null enquanto pendente.
    public DateTimeOffset? RevokedAt { get; private set; }

    public static EmailToken ForRegistration(string email, byte[] tokenHash, DateTimeOffset expiresAt) =>
        Create(EmailTokenPurpose.Registration, email, null, tokenHash, expiresAt);

    public static EmailToken ForPasswordReset(long userId, string email, byte[] tokenHash, DateTimeOffset expiresAt) =>
        Create(EmailTokenPurpose.PasswordReset, email, userId, tokenHash, expiresAt);

    private static EmailToken Create(EmailTokenPurpose purpose, string email, long? userId, byte[] tokenHash, DateTimeOffset expiresAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        if (tokenHash.Length != HashLength)
        {
            throw new ArgumentException($"O hash do token deve ter {HashLength} bytes.", nameof(tokenHash));
        }

        return new EmailToken(purpose, email, userId, tokenHash, expiresAt);
    }
}

internal enum EmailTokenPurpose
{
    Registration,
    PasswordReset,
}
