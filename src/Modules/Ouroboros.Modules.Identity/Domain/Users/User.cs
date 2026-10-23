using Ouroboros.BuildingBlocks.Domain;

namespace Ouroboros.Modules.Identity.Domain.Users;

internal sealed class User : Entity
{
    // Limite prático de um endereço de e-mail (RFC 5321).
    public const int EmailMaxLength = 254;

    private User(string email, string passwordHash, UserRole role)
    {
        Email = email;
        PasswordHash = passwordHash;
        Role = role;
        IsActive = true;
    }

    // E-mail normalizado (sem espaços nas pontas e em minúsculas): é a chave de busca e de unicidade.
    public string Email { get; private set; }

    public string PasswordHash { get; private set; }

    public UserRole Role { get; private set; }

    public bool IsActive { get; private set; }

    // Falhas de senha seguidas desde o último login bem-sucedido ou o último bloqueio. O incremento é
    // feito no banco, de forma atômica (veja AccountLockout), e não por este objeto.
    public int FailedLoginCount { get; private set; }

    // Fim do bloqueio por falhas de senha; null quando a conta nunca foi bloqueada ou já foi liberada.
    public DateTimeOffset? LockedUntil { get; private set; }

    public static User Create(string email, string passwordHash, UserRole role)
    {
        var normalizedEmail = NormalizeEmail(email);
        if (!EmailAddresses.IsValid(normalizedEmail))
        {
            throw new ArgumentException("O e-mail não é um endereço válido.", nameof(email));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        return new User(normalizedEmail, passwordHash, role);
    }

    public static string NormalizeEmail(string email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        return email.Trim().ToLowerInvariant();
    }

    public void ChangePasswordHash(string passwordHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        PasswordHash = passwordHash;
    }

    public bool IsLockedAt(DateTimeOffset now) => LockedUntil > now;

    // Login bem-sucedido zera o contador e remove um bloqueio já vencido.
    public void ResetFailedLogins()
    {
        FailedLoginCount = 0;
        LockedUntil = null;
    }
}
