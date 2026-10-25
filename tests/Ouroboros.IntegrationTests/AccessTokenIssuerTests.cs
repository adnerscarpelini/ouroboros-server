using System.Net.Http.Headers;
using System.Net.Http.Json;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Ouroboros.Identity.Application.Security;
using Ouroboros.Identity.Domain.Users;

namespace Ouroboros.IntegrationTests;

// O token emitido pelo Identity é aceito pela própria API e carrega sub, role e sid.
public sealed class AccessTokenIssuerTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    [Fact]
    public async Task Public_jwks_is_anonymous_cached_and_absent_from_admin_listener()
    {
        using var response = await Client.GetAsync("/api/v1/identity/keys", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        Assert.Equal(TimeSpan.FromMinutes(5), response.Headers.CacheControl?.MaxAge);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var keys = new Microsoft.IdentityModel.Tokens.JsonWebKeySet(json);
        var key = Assert.Single(keys.Keys);
        Assert.Equal("test-key", key.Kid);
        Assert.Equal("RS256", key.Alg);
        Assert.False(key.HasPrivateKey);
        using var admin = Api.CreateAdminClient();
        var token = Api.Services.GetRequiredService<IAccessTokenIssuer>().Issue(Guid.CreateVersion7(), UserRole.Admin, Guid.CreateVersion7());
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        using var hidden = await admin.GetAsync("/api/v1/identity/keys", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, hidden.StatusCode);
    }

    [Fact]
    public async Task Issued_token_is_accepted_and_carries_identity_claims()
    {
        var user = User.Create("cliente@example.com", "hash", UserRole.Customer);
        var sessionId = Guid.CreateVersion7();
        var token = Api.Services.GetRequiredService<IAccessTokenIssuer>().Issue(user.ExternalId, user.Role, sessionId);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/integration-probe/claims");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        var response = await Client.SendAsync(request, TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var claims = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>(TestContext.Current.CancellationToken);
        Assert.NotNull(claims);
        Assert.Equal(user.ExternalId.ToString(), claims["sub"]);
        Assert.Equal("Customer", claims["role"]);
        Assert.Equal(sessionId.ToString(), claims["sid"]);
    }

    [Theory]
    [InlineData("Customer", "ouroboros-api")]
    [InlineData("Admin", "ouroboros-backoffice")]
    public void Issued_token_uses_the_role_audience_and_access_token_lifetime(string roleName, string audience)
    {
        var token = Api.Services.GetRequiredService<IAccessTokenIssuer>()
            .Issue(Guid.CreateVersion7(), Enum.Parse<UserRole>(roleName), Guid.CreateVersion7());

        var jwt = new JsonWebToken(token.Token);
        Assert.Equal("RS256", jwt.Alg);
        Assert.Equal("test-key", jwt.Kid);
        Assert.Equal("ouroboros", jwt.Issuer);
        Assert.Equal([audience], jwt.Audiences);
        Assert.Equal(TimeSpan.FromMinutes(15), jwt.ValidTo - jwt.IssuedAt);
        Assert.Equal(token.ExpiresAt.UtcDateTime, jwt.ValidTo, TimeSpan.FromSeconds(1));
    }
}

// Sonda autenticada (sem [AllowAnonymous]) que devolve os claims do token.
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/integration-probe/claims")]
public sealed class ClaimsProbeController : ControllerBase
{
    [HttpGet]
    public Dictionary<string, string> Get() =>
        User.Claims.GroupBy(claim => claim.Type).ToDictionary(group => group.Key, group => group.First().Value);
}
