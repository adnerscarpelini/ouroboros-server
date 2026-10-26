using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Ouroboros.Identity.Application.Security;
using Ouroboros.Identity.Contracts;
using Ouroboros.Identity.Domain.Users;

namespace Ouroboros.Identity.Infrastructure.Security;

// Emite o access token (JWT RS256) com sub (ExternalId do usuário), role e sid (ExternalId da
// sessão), para a audiência do público do papel: Customer na audiência de cliente, Admin na de administrador.
internal sealed class AccessTokenIssuer(IOptions<JwtOptions> options, TimeProvider timeProvider, TokenKeyRing keys) : IAccessTokenIssuer
{
    private readonly JsonWebTokenHandler _handler = new();

    public AccessToken Issue(Guid userId, UserRole role, Guid sessionId)
    {
        var jwt = options.Value;
        var now = timeProvider.GetUtcNow();
        var expiresAt = now.Add(jwt.AccessTokenLifetime);

        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = jwt.Issuer,
            Audience = jwt.AudienceOf(role),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
                new Claim(IdentityClaims.Role, role.ToString()),
                new Claim(IdentityClaims.PrincipalType, "user"),
                new Claim(JwtRegisteredClaimNames.Sid, sessionId.ToString()),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7().ToString()),
            ]),
            SigningCredentials = keys.SigningCredentials(),
        });

        return new AccessToken(token, expiresAt);
    }
}
