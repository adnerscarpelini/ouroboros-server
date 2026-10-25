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
    public async Task Issued_token_is_accepted_and_carries_identity_claims()
    {
        var user = User.Create("admin@example.com", "hash", UserRole.Admin);
        var sessionId = Guid.CreateVersion7();
        var token = Api.Services.GetRequiredService<IAccessTokenIssuer>().Issue(user.ExternalId, user.Role, sessionId);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/integration-probe/claims");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        var response = await Client.SendAsync(request, TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var claims = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>(TestContext.Current.CancellationToken);
        Assert.NotNull(claims);
        Assert.Equal(user.ExternalId.ToString(), claims["sub"]);
        Assert.Equal("Admin", claims["role"]);
        Assert.Equal(sessionId.ToString(), claims["sid"]);
    }

    [Fact]
    public void Issued_token_uses_host_audience_and_access_token_lifetime()
    {
        var token = Api.Services.GetRequiredService<IAccessTokenIssuer>()
            .Issue(Guid.CreateVersion7(), UserRole.Customer, Guid.CreateVersion7());

        var jwt = new JsonWebToken(token.Token);
        Assert.Equal("HS256", jwt.Alg);
        Assert.Equal("ouroboros", jwt.Issuer);
        Assert.Equal(["ouroboros-api"], jwt.Audiences);
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
