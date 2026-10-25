using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Ouroboros.Identity.Application.Security;
using Ouroboros.Identity.Domain.Users;
using Ouroboros.Identity.Infrastructure.Persistence;

namespace Ouroboros.IntegrationTests;

// Os hosts agregadores seguem existindo até a #96. Estes testes mantêm a fumaça do que eles ainda expõem
// (login e consulta do próprio usuário em cada público); o comportamento completo é verificado na API de Identity.
public sealed class LegacyHostsSmokeTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private const string Password = "uma frase longa e secreta do usuário";

    [Fact]
    public async Task External_host_still_logs_a_customer_in_and_returns_the_user()
    {
        await using var host = new LegacyExternalHostFactory(ConnectionString);
        await CreateUserAsync(host, "cliente@example.com", UserRole.Customer);
        using var client = host.CreateClient();

        var token = await LoginAsync(client, "/api/v1/identity/sessions", "cliente@example.com");

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, "/api/v1/identity/me", token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(client, "/api/v1/admin/identity/me", token)).StatusCode);
    }

    [Fact]
    public async Task Backoffice_host_still_logs_an_admin_in_and_returns_the_user()
    {
        await using var host = new LegacyBackofficeHostFactory(ConnectionString);
        await CreateUserAsync(host, "oi@adnerscarpelini.com", UserRole.Admin);
        using var client = host.CreateClient();

        var token = await LoginAsync(client, "/api/v1/admin/identity/sessions", "oi@adnerscarpelini.com");

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, "/api/v1/admin/identity/me", token)).StatusCode);
    }

    private static async Task CreateUserAsync<TEntryPoint>(WebApplicationFactory<TEntryPoint> host, string email, UserRole role)
        where TEntryPoint : class
    {
        await using var scope = host.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        context.Users.Add(User.Create(email, scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(Password), role));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<string> LoginAsync(HttpClient client, string route, string email)
    {
        using var response = await client.PostAsJsonAsync(route, new { Email = email, Password }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return body.GetProperty("accessToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string route, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
