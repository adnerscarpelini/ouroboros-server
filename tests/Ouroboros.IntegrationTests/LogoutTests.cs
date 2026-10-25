using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Ouroboros.Identity.Application.Security;
using Ouroboros.Identity.Domain.Users;
using Ouroboros.Identity.Infrastructure.Persistence;
using Ouroboros.Identity.Infrastructure.Security;

namespace Ouroboros.IntegrationTests;

// Encerramento da sessão atual pelo refresh token, contra o PostgreSQL real.
public sealed class LogoutTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private const string LoginRoute = "/api/v1/identity/sessions";
    private const string RefreshRoute = "/api/v1/identity/sessions/refresh";
    private const string LogoutRoute = "/api/v1/identity/sessions/logout";
    private const string Email = "cliente@example.com";
    private const string Password = "uma frase longa e secreta do cliente";
    private const string InvalidRefreshTokenType =
        "https://github.com/adnerscarpelini/ouroboros-server/blob/main/docs/errors/identity.md#invalid-refresh-token";

    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    [Fact]
    public async Task Active_token_ends_the_session()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api);
        var token = RefreshTokenOf(await LoginAsync(client));

        var response = await PostAsync(client, LogoutRoute, token);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0L, await ActiveTokensAsync());
        await AssertInvalidRefreshTokenAsync(await PostAsync(client, RefreshRoute, token));
    }

    [Fact]
    public async Task Repeated_logout_returns_204_and_changes_nothing()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api);
        var token = RefreshTokenOf(await LoginAsync(client));
        await PostAsync(client, LogoutRoute, token);
        var revokedAt = await QueryScalarAsync<DateTime>("SELECT revoked_at FROM identity.refresh_tokens");
        _time.Advance(TimeSpan.FromMinutes(1));

        var response = await PostAsync(client, LogoutRoute, token);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(revokedAt, await QueryScalarAsync<DateTime>("SELECT revoked_at FROM identity.refresh_tokens"));
    }

    // Logout com um token rotacionado não é tratado como reuso: a sessão continua com o token atual.
    [Fact]
    public async Task Logout_with_a_rotated_token_does_not_revoke_the_session()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api);
        var first = RefreshTokenOf(await LoginAsync(client));
        var current = RefreshTokenOf(await ReadJsonAsync(await PostAsync(client, RefreshRoute, first)));

        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(client, LogoutRoute, first)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, RefreshRoute, current)).StatusCode);
    }

    [Fact]
    public async Task Expired_token_returns_204_and_changes_nothing()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api);
        var token = RefreshTokenOf(await LoginAsync(client));
        _time.Advance(TimeSpan.FromDays(7));

        var response = await PostAsync(client, LogoutRoute, token);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.refresh_tokens WHERE revoked_at IS NOT NULL"));
    }

    [Theory]
    [InlineData("""{"refreshToken":"token-que-nao-existe"}""")]
    [InlineData("""{"refreshToken":""}""")]
    [InlineData("""{"refreshToken":null}""")]
    [InlineData("{}")]
    public async Task Empty_or_unknown_token_returns_401(string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await Client.PostAsync(LogoutRoute, content, TestContext.Current.CancellationToken);

        await AssertInvalidRefreshTokenAsync(response);
    }

    [Fact]
    public async Task Ending_one_session_keeps_the_other_working()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api);
        var ended = RefreshTokenOf(await LoginAsync(client));
        var other = RefreshTokenOf(await LoginAsync(client));

        await PostAsync(client, LogoutRoute, ended);

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, RefreshRoute, other)).StatusCode);
    }

    [Fact]
    public async Task Backoffice_logout_ends_an_admin_session()
    {
        await using (var scope = Api.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            context.Users.Add(User.Create("oi@adnerscarpelini.com", scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(Password), UserRole.Admin));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var backoffice = new BackofficeIntegrationApiFactory(ConnectionString);
        using var client = backoffice.CreateClient();
        var login = await client.PostAsJsonAsync(
            "/api/v1/admin/identity/sessions", new { Email = "oi@adnerscarpelini.com", Password }, TestContext.Current.CancellationToken);
        var token = RefreshTokenOf(await ReadJsonAsync(login));

        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(client, "/api/v1/admin/identity/sessions/logout", token)).StatusCode);
        await AssertInvalidRefreshTokenAsync(await PostAsync(client, "/api/v1/admin/identity/sessions/refresh", token));
    }

    private ApiFactory CreateApi() => Api.WithWebHostBuilder(builder =>
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(_time);
            services.AddSingleton<IPasswordHasher>(new AspNetCorePasswordHasher(iterationCount: 1_000));
        }));

    private static async Task CreateUserAsync(ApiFactory api)
    {
        await using var scope = api.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        context.Users.Add(User.Create(Email, scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(Password), UserRole.Customer));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<JsonElement> LoginAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(LoginRoute, new { Email, Password }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string route, string refreshToken) =>
        client.PostAsJsonAsync(route, new { RefreshToken = refreshToken }, TestContext.Current.CancellationToken);

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    private static string RefreshTokenOf(JsonElement body) => body.GetProperty("refreshToken").GetString()!;

    private static async Task AssertInvalidRefreshTokenAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(InvalidRefreshTokenType, (await ReadJsonAsync(response)).GetProperty("type").GetString());
    }

    private Task<long> ActiveTokensAsync() =>
        QueryScalarAsync<long>("SELECT count(*) FROM identity.refresh_tokens WHERE revoked_at IS NULL");
}
