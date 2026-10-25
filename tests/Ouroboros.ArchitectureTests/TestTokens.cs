using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Ouroboros.ArchitectureTests;

// Gera tokens para os testes, válidos ou propositalmente inválidos, sem depender do emissor do
// Identity (que é exercitado nos testes de integração).
internal static class TestTokens
{
    // Chave fictícia de 256+ bits, só para testes.
    public const string SigningKey = "architecture-tests-signing-key-with-at-least-256-bits";

    public const string Issuer = "ouroboros";
    public const string ExternalAudience = "ouroboros-api";
    public const string BackofficeAudience = "ouroboros-backoffice";

    public static string Create(
        string audience,
        string role,
        string issuer = Issuer,
        string signingKey = SigningKey,
        TimeSpan? lifetime = null,
        bool unsigned = false,
        string algorithm = SecurityAlgorithms.RsaSha256,
        bool withExpiration = true,
        TimeSpan? notBeforeOffset = null,
        string principalType = "user")
    {
        var now = DateTime.UtcNow;
        var expires = now.Add(lifetime ?? TimeSpan.FromMinutes(15));
        var issuedAt = expires < now ? expires.AddMinutes(-1) : now;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = issuedAt,
            NotBefore = notBeforeOffset is { } offset ? now.Add(offset) : issuedAt,
            Expires = withExpiration ? expires : null,
            Subject = new ClaimsIdentity(
            [
                new Claim("sub", Guid.CreateVersion7().ToString()),
                new Claim("role", role),
                new Claim("principal_type", principalType),
                new Claim("sid", Guid.CreateVersion7().ToString()),
            ]),
        };

        if (!unsigned)
        {
            descriptor.SigningCredentials = new SigningCredentials(
                algorithm.StartsWith("HS", StringComparison.Ordinal)
                    ? new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey))
                    : signingKey == SigningKey ? TestSigningKeys.Key : new RsaSecurityKey(System.Security.Cryptography.RSA.Create(2048)) { KeyId = "other-key" },
                algorithm);
        }

        // Sem as datas padrão do handler, para poder emitir um token sem exp.
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = withExpiration }.CreateToken(descriptor);
    }
}
