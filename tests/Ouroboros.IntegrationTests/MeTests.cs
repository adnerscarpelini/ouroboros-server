using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Ouroboros.Identity.Application.Security;
using Ouroboros.Identity.Domain.Users;
using Ouroboros.Identity.Infrastructure.Persistence;
using Ouroboros.Identity.Infrastructure.Security;

namespace Ouroboros.IntegrationTests;

// Consulta do próprio usuário nos dois hosts, contra o PostgreSQL real.
public sealed class MeTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private const string Password = "uma frase longa e secreta do cliente";

    // Lista permitida: adicionar um campo à resposta sem atualizar esta lista reprova o teste.
    private static readonly string[] AllowedFields = ["email", "id", "role"];

    [Fact]
    public async Task Active_user_gets_id_email_and_role()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        var userId = await CreateUserAsync(api, "cliente@example.com", UserRole.Customer);
        var accessToken = await LoginAsync(client, "/api/v1/identity/sessions", "cliente@example.com");

        var response = await GetMeAsync(client, "/api/v1/identity/me", accessToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(userId, body.GetProperty("id").GetGuid());
        Assert.Equal("cliente@example.com", body.GetProperty("email").GetString());
        Assert.Equal("Customer", body.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Response_has_only_the_allowed_fields()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api, "cliente@example.com", UserRole.Customer);
        var accessToken = await LoginAsync(client, "/api/v1/identity/sessions", "cliente@example.com");

        var response = await GetMeAsync(client, "/api/v1/identity/me", accessToken);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(AllowedFields, body.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        var text = body.GetRawText();
        Assert.DoesNotContain(accessToken, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task User_that_became_inactive_gets_401()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api, "cliente@example.com", UserRole.Customer);
        var accessToken = await LoginAsync(client, "/api/v1/identity/sessions", "cliente@example.com");
        await ExecuteAsync("UPDATE identity.users SET is_active = false WHERE email = 'cliente@example.com'");

        var response = await GetMeAsync(client, "/api/v1/identity/me", accessToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    // O papel vem do banco: um admin rebaixado deixa de ser aceito no backoffice antes de o token expirar.
    [Fact]
    public async Task Admin_demoted_in_the_database_gets_401_in_the_backoffice()
    {
        await CreateUserAsync(Api, "oi@adnerscarpelini.com", UserRole.Admin);
        await using var backoffice = new BackofficeIntegrationApiFactory(ConnectionString);
        using var client = backoffice.CreateClient();
        var accessToken = await LoginAsync(client, "/api/v1/admin/identity/sessions", "oi@adnerscarpelini.com");
        await ExecuteAsync("UPDATE identity.users SET role = 'Customer' WHERE email = 'oi@adnerscarpelini.com'");

        var response = await GetMeAsync(client, "/api/v1/admin/identity/me", accessToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("nao-e-um-jwt")]
    public async Task Missing_or_invalid_token_gets_401(string? accessToken)
    {
        var response = await GetMeAsync(Client, "/api/v1/identity/me", accessToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Admin_gets_own_data_through_the_backoffice()
    {
        var adminId = await CreateUserAsync(Api, "oi@adnerscarpelini.com", UserRole.Admin);
        await using var backoffice = new BackofficeIntegrationApiFactory(ConnectionString);
        using var client = backoffice.CreateClient();
        var accessToken = await LoginAsync(client, "/api/v1/admin/identity/sessions", "oi@adnerscarpelini.com");

        var response = await GetMeAsync(client, "/api/v1/admin/identity/me", accessToken);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(adminId, body.GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("oi@adnerscarpelini.com", body.GetProperty("email").GetString());
        Assert.Equal("Admin", body.GetProperty("role").GetString());
        Assert.Equal(AllowedFields, body.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(false, "missing")]
    [InlineData(true, "missing")]
    [InlineData(false, "inactive")]
    [InlineData(true, "inactive")]
    [InlineData(false, "wrong-role")]
    [InlineData(true, "wrong-role")]
    public async Task Host_rechecks_user_state_with_a_valid_token(bool isBackoffice, string state)
    {
        var role = isBackoffice ? UserRole.Admin : UserRole.Customer;
        var userId = state == "missing"
            ? Guid.CreateVersion7()
            : await CreateUserAsync(Api, "user@example.com", role);

        await using var backoffice = new BackofficeIntegrationApiFactory(ConnectionString);
        var services = isBackoffice ? backoffice.Services : Api.Services;
        var token = services.GetRequiredService<IAccessTokenIssuer>()
            .Issue(userId, role, Guid.CreateVersion7()).Token;

        if (state == "inactive")
        {
            await ExecuteAsync("UPDATE identity.users SET is_active = false WHERE email = 'user@example.com'");
        }
        else if (state == "wrong-role")
        {
            var changedRole = isBackoffice ? "Customer" : "Admin";
            await ExecuteAsync($"UPDATE identity.users SET role = '{changedRole}' WHERE email = 'user@example.com'");
        }

        using var client = isBackoffice ? backoffice.CreateClient() : Api.CreateClient();
        var route = isBackoffice ? "/api/v1/admin/identity/me" : "/api/v1/identity/me";
        using var response = await GetMeAsync(client, route, token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Me_rejects_token_from_the_other_host(bool targetBackoffice)
    {
        var role = targetBackoffice ? UserRole.Admin : UserRole.Customer;
        var userId = await CreateUserAsync(Api, "user@example.com", role);
        await using var backoffice = new BackofficeIntegrationApiFactory(ConnectionString);
        var issuerServices = targetBackoffice ? Api.Services : backoffice.Services;
        var token = issuerServices.GetRequiredService<IAccessTokenIssuer>()
            .Issue(userId, role, Guid.CreateVersion7()).Token;

        using var client = targetBackoffice ? backoffice.CreateClient() : Api.CreateClient();
        var route = targetBackoffice ? "/api/v1/admin/identity/me" : "/api/v1/identity/me";
        using var response = await GetMeAsync(client, route, token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
    }

    private WebApplicationFactory<Program> CreateApi() => Api.WithWebHostBuilder(builder =>
        builder.ConfigureTestServices(services =>
            services.AddSingleton<IPasswordHasher>(new AspNetCorePasswordHasher(iterationCount: 1_000))));

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

    private static async Task<string> LoginAsync(HttpClient client, string route, string email)
    {
        var response = await client.PostAsJsonAsync(route, new { Email = email, Password }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return body.GetProperty("accessToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> GetMeAsync(HttpClient client, string route, string? accessToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
