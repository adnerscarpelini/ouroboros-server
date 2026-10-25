using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Ouroboros.Identity.Application.Security;
using Ouroboros.Identity.Domain.Users;
using Ouroboros.Identity.Infrastructure.Persistence;
using Ouroboros.Identity.Infrastructure.Security;

namespace Ouroboros.IntegrationTests;

// Renovação do access token com rotação do refresh token, contra o PostgreSQL real. O relógio é
// falso, para expirar o refresh token, e o hasher usa poucas iterações, para os testes serem rápidos.
public sealed class RefreshSessionTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private const string LoginRoute = "/api/v1/identity/sessions";
    private const string RefreshRoute = "/api/v1/identity/sessions/refresh";
    private const string BackofficeLoginRoute = "/api/v1/admin/identity/sessions";
    private const string BackofficeRefreshRoute = "/api/v1/admin/identity/sessions/refresh";
    private const string CustomerEmail = "cliente@example.com";
    private const string AdminEmail = "oi@adnerscarpelini.com";
    private const string Password = "uma frase longa e secreta do cliente";
    private const string InvalidRefreshTokenType =
        "https://github.com/adnerscarpelini/ouroboros-server/blob/main/docs/errors/identity.md#invalid-refresh-token";

    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    [Fact]
    public async Task Active_token_is_rotated_into_a_new_pair_in_the_same_session()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api, CustomerEmail, UserRole.Customer);
        var login = await LoginAsync(client, LoginRoute, CustomerEmail);
        _time.Advance(TimeSpan.FromMinutes(10));

        var response = await RefreshAsync(client, RefreshRoute, RefreshTokenOf(login));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var renewed = await ReadJsonAsync(response);
        Assert.NotEqual(RefreshTokenOf(login), RefreshTokenOf(renewed));
        Assert.NotEqual(login.GetProperty("accessToken").GetString(), renewed.GetProperty("accessToken").GetString());
        Assert.Equal(SessionIdOf(login), SessionIdOf(renewed));
        Assert.NotNull(await RevokedAtAsync(RefreshTokenOf(login)));
        Assert.Null(await RevokedAtAsync(RefreshTokenOf(renewed)));

        // Validade cheia contada a partir da renovação.
        Assert.Equal(_time.GetUtcNow().AddDays(7), renewed.GetProperty("refreshTokenExpiresAt").GetDateTimeOffset(), TimeSpan.FromSeconds(1));
        Assert.Equal(1L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.sessions"));
        Assert.Equal(2L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.refresh_tokens"));
    }

    [Fact]
    public async Task Rotated_token_cannot_be_used_again()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api, CustomerEmail, UserRole.Customer);
        var login = await LoginAsync(client, LoginRoute, CustomerEmail);
        await RefreshAsync(client, RefreshRoute, RefreshTokenOf(login));

        await AssertInvalidRefreshTokenAsync(await RefreshAsync(client, RefreshRoute, RefreshTokenOf(login)));
    }

    [Theory]
    [InlineData("""{"refreshToken":"token-que-nao-existe"}""")]
    [InlineData("""{"refreshToken":""}""")]
    [InlineData("""{"refreshToken":"   "}""")]
    [InlineData("""{"refreshToken":null}""")]
    [InlineData("{}")]
    public async Task Empty_or_unknown_token_returns_401(string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await Client.PostAsync(RefreshRoute, content, TestContext.Current.CancellationToken);

        await AssertInvalidRefreshTokenAsync(response);
    }

    [Fact]
    public async Task Expired_token_returns_401_and_revokes_nothing()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api, CustomerEmail, UserRole.Customer);
        var login = await LoginAsync(client, LoginRoute, CustomerEmail);
        _time.Advance(TimeSpan.FromDays(7));

        await AssertInvalidRefreshTokenAsync(await RefreshAsync(client, RefreshRoute, RefreshTokenOf(login)));
        Assert.Null(await RevokedAtAsync(RefreshTokenOf(login)));
        Assert.Equal(1L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.refresh_tokens"));
    }

    [Fact]
    public async Task Inactive_user_gets_401_and_the_token_is_not_consumed()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api, CustomerEmail, UserRole.Customer);
        var login = await LoginAsync(client, LoginRoute, CustomerEmail);
        await ExecuteAsync($"UPDATE identity.users SET is_active = false WHERE email = '{CustomerEmail}'");

        await AssertInvalidRefreshTokenAsync(await RefreshAsync(client, RefreshRoute, RefreshTokenOf(login)));
        Assert.Null(await RevokedAtAsync(RefreshTokenOf(login)));

        await ExecuteAsync($"UPDATE identity.users SET is_active = true WHERE email = '{CustomerEmail}'");
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(client, RefreshRoute, RefreshTokenOf(login))).StatusCode);
    }

    // Uma falha ao gravar o token novo (aqui, provocada por um trigger) desfaz a revogação do antigo.
    [Fact]
    public async Task Failure_writing_the_new_token_keeps_the_previous_one_valid()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api, CustomerEmail, UserRole.Customer);
        var login = await LoginAsync(client, LoginRoute, CustomerEmail);
        await ExecuteAsync("""
            CREATE FUNCTION identity.fail_insert() RETURNS trigger AS $$
            BEGIN RAISE EXCEPTION 'falha simulada ao gravar o refresh token'; END;
            $$ LANGUAGE plpgsql;
            CREATE TRIGGER fail_insert BEFORE INSERT ON identity.refresh_tokens
            FOR EACH ROW EXECUTE FUNCTION identity.fail_insert();
            """);

        var failed = await RefreshAsync(client, RefreshRoute, RefreshTokenOf(login));

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Null(await RevokedAtAsync(RefreshTokenOf(login)));
        Assert.Equal(1L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.refresh_tokens"));

        await ExecuteAsync("DROP TRIGGER fail_insert ON identity.refresh_tokens");
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(client, RefreshRoute, RefreshTokenOf(login))).StatusCode);
    }

    [Fact]
    public async Task New_access_token_uses_the_role_stored_in_the_database()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        var userId = await CreateUserAsync(api, CustomerEmail, UserRole.Customer);
        var login = await LoginAsync(client, LoginRoute, CustomerEmail);

        var renewed = await ReadJsonAsync(await RefreshAsync(client, RefreshRoute, RefreshTokenOf(login)));
        var jwt = new JsonWebToken(renewed.GetProperty("accessToken").GetString());
        Assert.Equal("Customer", jwt.GetClaim("role").Value);
        Assert.Equal(userId.ToString(), jwt.Subject);

        // Papel alterado no banco: a API externa não emite mais um token com o papel antigo.
        await ExecuteAsync($"UPDATE identity.users SET role = 'Admin' WHERE email = '{CustomerEmail}'");
        await AssertInvalidRefreshTokenAsync(await RefreshAsync(client, RefreshRoute, RefreshTokenOf(renewed)));
        Assert.Null(await RevokedAtAsync(RefreshTokenOf(renewed)));
    }

    [Fact]
    public async Task Admin_session_renews_only_through_the_backoffice()
    {
        await CreateUserAsync(Api, AdminEmail, UserRole.Admin);
        await using var backoffice = new BackofficeIntegrationApiFactory(ConnectionString);
        using var backofficeClient = backoffice.CreateClient();
        var login = await LoginAsync(backofficeClient, BackofficeLoginRoute, AdminEmail);

        await AssertInvalidRefreshTokenAsync(await RefreshAsync(Client, RefreshRoute, RefreshTokenOf(login)));
        Assert.Null(await RevokedAtAsync(RefreshTokenOf(login)));

        var renewed = await ReadJsonAsync(await RefreshAsync(backofficeClient, BackofficeRefreshRoute, RefreshTokenOf(login)));
        var jwt = new JsonWebToken(renewed.GetProperty("accessToken").GetString());
        Assert.Equal(["ouroboros-backoffice"], jwt.Audiences);
        Assert.Equal("Admin", jwt.GetClaim("role").Value);
    }

    [Fact]
    public async Task Customer_session_cannot_renew_through_the_backoffice()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api, CustomerEmail, UserRole.Customer);
        var login = await LoginAsync(client, LoginRoute, CustomerEmail);
        await using var backoffice = new BackofficeIntegrationApiFactory(ConnectionString);
        using var backofficeClient = backoffice.CreateClient();

        await AssertInvalidRefreshTokenAsync(await RefreshAsync(backofficeClient, BackofficeRefreshRoute, RefreshTokenOf(login)));
        Assert.Null(await RevokedAtAsync(RefreshTokenOf(login)));
    }

    [Fact]
    public async Task Concurrent_renewals_with_the_same_token_have_a_single_winner()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api, CustomerEmail, UserRole.Customer);
        var refreshToken = RefreshTokenOf(await LoginAsync(client, LoginRoute, CustomerEmail));

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => RefreshAsync(client, RefreshRoute, refreshToken)));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.All(responses.Where(response => response.StatusCode != HttpStatusCode.OK), response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
        Assert.Equal(2L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.refresh_tokens"));
    }

    [Fact]
    public async Task Configured_token_lifetimes_are_preserved_on_login_and_refresh()
    {
        await using var api = CreateApi().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Authentication:Jwt:AccessTokenLifetime"] = "00:09:00",
                    ["Authentication:Jwt:RefreshTokenLifetime"] = "2.00:00:00",
                })));
        using var client = api.CreateClient();
        await CreateUserAsync(api, CustomerEmail, UserRole.Customer);

        var login = await LoginAsync(client, LoginRoute, CustomerEmail);
        Assert.Equal(_time.GetUtcNow().AddDays(2), login.GetProperty("refreshTokenExpiresAt").GetDateTimeOffset());
        Assert.Equal(_time.GetUtcNow().AddMinutes(9), login.GetProperty("accessTokenExpiresAt").GetDateTimeOffset());

        _time.Advance(TimeSpan.FromMinutes(5));
        var response = await RefreshAsync(client, RefreshRoute, RefreshTokenOf(login));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var renewed = await ReadJsonAsync(response);
        Assert.Equal(_time.GetUtcNow().AddDays(2), renewed.GetProperty("refreshTokenExpiresAt").GetDateTimeOffset());
        Assert.Equal(_time.GetUtcNow().AddMinutes(9), renewed.GetProperty("accessTokenExpiresAt").GetDateTimeOffset());
        Assert.Equal(SessionIdOf(login), SessionIdOf(renewed));
        Assert.NotEqual(RefreshTokenOf(login), RefreshTokenOf(renewed));
    }

    private WebApplicationFactory<Program> CreateApi() => Api.WithWebHostBuilder(builder =>
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(_time);
            services.AddSingleton<IPasswordHasher>(new AspNetCorePasswordHasher(iterationCount: 1_000));
        }));

    private static async Task<Guid> CreateUserAsync<TEntryPoint>(WebApplicationFactory<TEntryPoint> api, string email, UserRole role)
        where TEntryPoint : class
    {
        await using var scope = api.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var user = User.Create(email, scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(Password), role);
        context.Users.Add(user);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return user.ExternalId;
    }

    private static async Task<JsonElement> LoginAsync(HttpClient client, string route, string email)
    {
        var response = await client.PostAsJsonAsync(route, new { Email = email, Password }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    private static Task<HttpResponseMessage> RefreshAsync(HttpClient client, string route, string refreshToken) =>
        client.PostAsJsonAsync(route, new { RefreshToken = refreshToken }, TestContext.Current.CancellationToken);

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    private static string RefreshTokenOf(JsonElement body) => body.GetProperty("refreshToken").GetString()!;

    private static string SessionIdOf(JsonElement body) =>
        new JsonWebToken(body.GetProperty("accessToken").GetString()).GetClaim("sid").Value;

    private static async Task AssertInvalidRefreshTokenAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await ReadJsonAsync(response);
        Assert.Equal(InvalidRefreshTokenType, body.GetProperty("type").GetString());
        Assert.False(body.TryGetProperty("accessToken", out _));
    }

    private Task<DateTime?> RevokedAtAsync(string refreshToken) =>
        QueryScalarAsync<DateTime?>(
            $"SELECT revoked_at FROM identity.refresh_tokens WHERE token_hash = decode('{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)))}', 'hex')");
}
