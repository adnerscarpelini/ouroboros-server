using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Ouroboros.Modules.Identity.Application.Security;
using Ouroboros.Modules.Identity.Domain.Users;
using Ouroboros.Modules.Identity.Infrastructure.Persistence;
using Ouroboros.Modules.Identity.Infrastructure.Security;

namespace Ouroboros.IntegrationTests;

// Encerramento de todas as sessões do usuário autenticado, contra o PostgreSQL real.
public sealed class LogoutAllTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private const string LoginRoute = "/api/v1/identity/sessions";
    private const string RefreshRoute = "/api/v1/identity/sessions/refresh";
    private const string LogoutAllRoute = "/api/v1/identity/sessions/logout-all";
    private const string Password = "uma frase longa e secreta do cliente";

    [Fact]
    public async Task All_sessions_of_the_user_stop_renewing()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api, "cliente@example.com", UserRole.Customer);
        var first = await LoginAsync(client, LoginRoute, "cliente@example.com");
        var second = await LoginAsync(client, LoginRoute, "cliente@example.com");
        var renewed = await ReadJsonAsync(await RefreshAsync(client, RefreshRoute, RefreshTokenOf(second)));

        var response = await LogoutAllAsync(client, LogoutAllRoute, AccessTokenOf(first));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.refresh_tokens WHERE revoked_at IS NULL"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(client, RefreshRoute, RefreshTokenOf(first))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(client, RefreshRoute, RefreshTokenOf(renewed))).StatusCode);
    }

    [Fact]
    public async Task User_without_active_sessions_gets_204()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api, "cliente@example.com", UserRole.Customer);
        var accessToken = AccessTokenOf(await LoginAsync(client, LoginRoute, "cliente@example.com"));
        await LogoutAllAsync(client, LogoutAllRoute, accessToken);

        var again = await LogoutAllAsync(client, LogoutAllRoute, accessToken);

        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("nao-e-um-jwt")]
    public async Task Missing_or_invalid_token_returns_401(string? accessToken)
    {
        var response = await LogoutAllAsync(Client, LogoutAllRoute, accessToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Other_users_are_not_affected()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api, "cliente@example.com", UserRole.Customer);
        await CreateUserAsync(api, "outro@example.com", UserRole.Customer);
        var mine = await LoginAsync(client, LoginRoute, "cliente@example.com");
        var theirs = await LoginAsync(client, LoginRoute, "outro@example.com");

        await LogoutAllAsync(client, LogoutAllRoute, AccessTokenOf(mine));

        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(client, RefreshRoute, RefreshTokenOf(theirs))).StatusCode);
    }

    [Fact]
    public async Task Admin_ends_all_sessions_through_the_backoffice()
    {
        await CreateUserAsync(Api, "oi@adnerscarpelini.com", UserRole.Admin);
        await using var backoffice = new BackofficeIntegrationApiFactory(ConnectionString);
        using var client = backoffice.CreateClient();
        var first = await LoginAsync(client, "/api/v1/admin/identity/sessions", "oi@adnerscarpelini.com");
        var second = await LoginAsync(client, "/api/v1/admin/identity/sessions", "oi@adnerscarpelini.com");

        var response = await LogoutAllAsync(client, "/api/v1/admin/identity/sessions/logout-all", AccessTokenOf(first));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(client, "/api/v1/admin/identity/sessions/refresh", RefreshTokenOf(second))).StatusCode);
    }

    // O access token da API externa tem outra audiência e não vale no backoffice.
    [Fact]
    public async Task Customer_token_is_rejected_by_the_backoffice()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api, "cliente@example.com", UserRole.Customer);
        var login = await LoginAsync(client, LoginRoute, "cliente@example.com");
        await using var backoffice = new BackofficeIntegrationApiFactory(ConnectionString);
        using var backofficeClient = backoffice.CreateClient();

        var response = await LogoutAllAsync(backofficeClient, "/api/v1/admin/identity/sessions/logout-all", AccessTokenOf(login));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(1L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.refresh_tokens WHERE revoked_at IS NULL"));
    }

    private WebApplicationFactory<Program> CreateApi() => Api.WithWebHostBuilder(builder =>
        builder.ConfigureTestServices(services =>
            services.AddSingleton<IPasswordHasher>(new AspNetCorePasswordHasher(iterationCount: 1_000))));

    private static async Task CreateUserAsync<TEntryPoint>(WebApplicationFactory<TEntryPoint> api, string email, UserRole role)
        where TEntryPoint : class
    {
        await using var scope = api.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        context.Users.Add(User.Create(email, scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(Password), role));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<JsonElement> LoginAsync(HttpClient client, string route, string email)
    {
        var response = await client.PostAsJsonAsync(route, new { Email = email, Password }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    private static Task<HttpResponseMessage> RefreshAsync(HttpClient client, string route, string refreshToken) =>
        client.PostAsJsonAsync(route, new { RefreshToken = refreshToken }, TestContext.Current.CancellationToken);

    private static async Task<HttpResponseMessage> LogoutAllAsync(HttpClient client, string route, string? accessToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route);
        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    private static string RefreshTokenOf(JsonElement body) => body.GetProperty("refreshToken").GetString()!;

    private static string AccessTokenOf(JsonElement body) => body.GetProperty("accessToken").GetString()!;
}
