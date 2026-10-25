using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Ouroboros.ArchitectureTests;

public sealed class PrincipalAmbiguityTests(OuroborosApiFactory external, BackofficeApiFactory backoffice)
    : IClassFixture<OuroborosApiFactory>, IClassFixture<BackofficeApiFactory>
{
    [Fact]
    public async Task User_and_service_principal_in_same_token_is_rejected()
    {
        var token = Create(TestTokens.ExternalAudience, ["Customer"], ["user", "service"]);
        using var client = ProbeHost.CreateClient(external, new ProbeCallCounter());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.GetAsync("/api/v1/secured-probe", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Contradictory_roles_are_rejected_before_admin_authorization()
    {
        var token = Create(TestTokens.BackofficeAudience, ["Customer", "Admin"], ["user"]);
        using var client = ProbeHost.CreateClient(backoffice, new ProbeCallCounter());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.GetAsync("/api/v1/admin/secured-probe", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static string Create(string audience, string[] roles, string[] kinds) => new JsonWebTokenHandler().CreateToken(
        new SecurityTokenDescriptor
        {
            Issuer = TestTokens.Issuer,
            Audience = audience,
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(TestSigningKeys.Key, SecurityAlgorithms.RsaSha256),
            Subject = new ClaimsIdentity(
                new[] { new Claim("sub", Guid.NewGuid().ToString()), new Claim("sid", Guid.NewGuid().ToString()) }
                    .Concat(roles.Select(role => new Claim("role", role)))
                    .Concat(kinds.Select(kind => new Claim("principal_type", kind)))),
        });
}
