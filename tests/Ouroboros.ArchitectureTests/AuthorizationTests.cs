using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.Tokens;

namespace Ouroboros.ArchitectureTests;

public sealed class AuthorizationTests(OuroborosApiFactory external, BackofficeApiFactory backoffice)
    : IClassFixture<OuroborosApiFactory>, IClassFixture<BackofficeApiFactory>
{
    private const string Customer = "Customer";
    private const string Admin = "Admin";

    [Fact]
    public async Task Endpoint_without_annotation_requires_token()
    {
        var response = await GetAsync(external, "/api/v1/secured-probe", token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Valid_token_is_accepted()
    {
        var response = await GetAsync(external, "/api/v1/secured-probe", TestTokens.Create(TestTokens.ExternalAudience, Customer));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public static TheoryData<string, string> InvalidTokens => new()
    {
        { "alg=none", TestTokens.Create(TestTokens.ExternalAudience, Customer, unsigned: true) },
        { "outra chave", TestTokens.Create(TestTokens.ExternalAudience, Customer, signingKey: "uma-chave-diferente-tambem-com-mais-de-256-bits") },
        { "expirado", TestTokens.Create(TestTokens.ExternalAudience, Customer, lifetime: TimeSpan.FromMinutes(-5)) },
        { "outro emissor", TestTokens.Create(TestTokens.ExternalAudience, Customer, issuer: "outro-emissor") },
        { "audiência do backoffice", TestTokens.Create(TestTokens.BackofficeAudience, Customer) },
        { "HS384 em vez de HS256", TestTokens.Create(TestTokens.ExternalAudience, Customer, algorithm: SecurityAlgorithms.HmacSha384) },
        { "sem exp", TestTokens.Create(TestTokens.ExternalAudience, Customer, withExpiration: false) },
        { "nbf no futuro além da tolerância", TestTokens.Create(TestTokens.ExternalAudience, Customer, notBeforeOffset: TimeSpan.FromSeconds(60)) },
        { "expirado além da tolerância de 30 s", TestTokens.Create(TestTokens.ExternalAudience, Customer, lifetime: TimeSpan.FromSeconds(-45)) },
    };

    // A tolerância de relógio é de 30 s: um token vencido há 15 s ainda é aceito.
    [Fact]
    public async Task Token_expired_within_clock_skew_is_accepted()
    {
        var response = await GetAsync(
            external,
            "/api/v1/secured-probe",
            TestTokens.Create(TestTokens.ExternalAudience, Customer, lifetime: TimeSpan.FromSeconds(-15)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Rejected_token_response_does_not_explain_the_reason()
    {
        var response = await GetAsync(
            external,
            "/api/v1/secured-probe",
            TestTokens.Create(TestTokens.ExternalAudience, Customer, lifetime: TimeSpan.FromMinutes(-5)));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("error_description", string.Join(",", response.Headers.WwwAuthenticate.Select(header => header.ToString())));
    }

    [Theory]
    [MemberData(nameof(InvalidTokens))]
    public async Task Invalid_token_is_rejected(string reason, string token)
    {
        var response = await GetAsync(external, "/api/v1/secured-probe", token);

        Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{reason}: {(int)response.StatusCode}");
    }

    [Fact]
    public async Task User_without_required_role_gets_403()
    {
        var response = await GetAsync(external, "/api/v1/secured-probe/admin-only", TestTokens.Create(TestTokens.ExternalAudience, Customer));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/admin/secured-probe")]
    [InlineData("/api/v1/admin/secured-probe/with-policy")]
    public async Task Customer_gets_403_on_any_backoffice_endpoint(string path)
    {
        var response = await GetAsync(backoffice, path, TestTokens.Create(TestTokens.BackofficeAudience, Customer));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/admin/secured-probe")]
    [InlineData("/api/v1/admin/secured-probe/with-policy")]
    public async Task Admin_is_authorized_on_backoffice_endpoints(string path)
    {
        var response = await GetAsync(backoffice, path, TestTokens.Create(TestTokens.BackofficeAudience, Admin));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Token_of_external_audience_is_rejected_by_backoffice()
    {
        var response = await GetAsync(backoffice, "/api/v1/admin/secured-probe", TestTokens.Create(TestTokens.ExternalAudience, Admin));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Token_of_backoffice_audience_is_rejected_by_external_api()
    {
        var response = await GetAsync(external, "/api/v1/secured-probe", TestTokens.Create(TestTokens.BackofficeAudience, Admin));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    public static TheoryData<string?, string> InvalidSigningKeys => new()
    {
        { null, "SigningKey' não foi configurada" },
        { "", "SigningKey' não foi configurada" },
        { "curta-demais-para-hs256", "menos de 256 bits" },
        { "                                        ", "SigningKey' não foi configurada" },
    };

    [Theory]
    [MemberData(nameof(InvalidSigningKeys))]
    public void Host_fails_to_start_without_valid_signing_key(string? signingKey, string expectedMessage)
    {
        using var factory = new OuroborosApiFactory(
            TestHostFactory<Program>.FakeConnectionString,
            new Dictionary<string, string?> { ["Authentication:Jwt:SigningKey"] = signingKey });

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(expectedMessage, exception.Message);
    }

    private static async Task<HttpResponseMessage> GetAsync<TEntryPoint>(
        WebApplicationFactory<TEntryPoint> factory,
        string path,
        string? token)
        where TEntryPoint : class
    {
        using var client = ProbeHost.CreateClient(factory, new ProbeCallCounter());
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.GetAsync(path, TestContext.Current.CancellationToken);
    }
}
