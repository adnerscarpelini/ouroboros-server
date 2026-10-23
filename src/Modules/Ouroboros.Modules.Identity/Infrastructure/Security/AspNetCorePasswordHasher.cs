using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Ouroboros.Modules.Identity.Application.Security;
using Ouroboros.Modules.Identity.Domain.Users;

namespace Ouroboros.Modules.Identity.Infrastructure.Security;

// Usa só o PasswordHasher do ASP.NET Core Identity (PBKDF2 com HMAC-SHA512, formato V3), sem o
// restante do Identity. O padrão do framework é 100.000 iterações; a OWASP recomenda 210.000 para
// PBKDF2-HMAC-SHA512.
internal sealed class AspNetCorePasswordHasher : IPasswordHasher
{
    public const int IterationCount = 210_000;

    private readonly PasswordHasher<User> _hasher;

    public AspNetCorePasswordHasher()
        : this(IterationCount)
    {
    }

    internal AspNetCorePasswordHasher(int iterationCount) =>
        _hasher = new PasswordHasher<User>(Options.Create(new PasswordHasherOptions
        {
            CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
            IterationCount = iterationCount,
        }));

    // O usuário não participa do cálculo no PasswordHasher padrão; o parâmetro existe só pela assinatura.
    // A senha é normalizada (NFKC) antes do hash e da verificação, como pede o NIST.
    public string Hash(string password) => _hasher.HashPassword(null!, PasswordPolicy.Normalize(password));

    public PasswordVerificationOutcome Verify(string passwordHash, string providedPassword) =>
        _hasher.VerifyHashedPassword(null!, passwordHash, PasswordPolicy.Normalize(providedPassword)) switch
        {
            PasswordVerificationResult.Success => PasswordVerificationOutcome.Succeeded,
            PasswordVerificationResult.SuccessRehashNeeded => PasswordVerificationOutcome.SucceededRehashNeeded,
            _ => PasswordVerificationOutcome.Failed,
        };
}
