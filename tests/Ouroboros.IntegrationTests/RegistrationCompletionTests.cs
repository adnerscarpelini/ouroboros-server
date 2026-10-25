using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Ouroboros.Identity.Application.Notifications;
using Ouroboros.Identity.Application.Security;
using Ouroboros.Identity.Domain.Users;
using Ouroboros.Identity.Infrastructure.Persistence;
using Ouroboros.Identity.Infrastructure.Security;

namespace Ouroboros.IntegrationTests;

// Conclusão do cadastro de cliente com o token do link, contra o PostgreSQL real.
public sealed class RegistrationCompletionTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private const string RequestRoute = "/api/v1/identity/registrations";
    private const string CompleteRoute = "/api/v1/identity/registrations/complete";
    private const string Email = "novo@example.com";
    private const string Password = "uma frase longa e secreta do cliente recente";
    private const string InvalidTokenType =
        "https://github.com/adnerscarpelini/ouroboros-server/blob/main/docs/errors/identity.md#invalid-registration-token";
    private const string PasswordRejectedType =
        "https://github.com/adnerscarpelini/ouroboros-server/blob/main/docs/errors/identity.md#password-rejected";

    private readonly CapturingEmailSender _emails = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    [Fact]
    public async Task Valid_token_creates_an_active_customer_that_can_log_in()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        var token = await RequestTokenAsync(client);

        var response = await CompleteAsync(client, token, Password);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Customer", await QueryScalarAsync<string>($"SELECT role FROM identity.users WHERE email = '{Email}'"));
        Assert.True(await QueryScalarAsync<bool>($"SELECT is_active FROM identity.users WHERE email = '{Email}'"));
        Assert.NotNull(await QueryScalarAsync<DateTime?>("SELECT used_at FROM identity.email_tokens"));
        Assert.Equal(0L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.sessions"));

        var login = await client.PostAsJsonAsync("/api/v1/identity/sessions", new { Email, Password }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task Used_token_is_rejected()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        var token = await RequestTokenAsync(client);
        await CompleteAsync(client, token, Password);

        await AssertInvalidTokenAsync(await CompleteAsync(client, token, Password + " outra"));
        Assert.Equal(1L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.users"));
    }

    [Fact]
    public async Task Expired_token_is_rejected()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        var token = await RequestTokenAsync(client);
        _time.Advance(TimeSpan.FromHours(24));

        await AssertInvalidTokenAsync(await CompleteAsync(client, token, Password));
        Assert.Equal(0L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.users"));
    }

    [Fact]
    public async Task Token_invalidated_by_a_newer_request_is_rejected()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        var first = await RequestTokenAsync(client);
        var second = await RequestTokenAsync(client);

        await AssertInvalidTokenAsync(await CompleteAsync(client, first, Password));
        Assert.Equal(HttpStatusCode.NoContent, (await CompleteAsync(client, second, Password)).StatusCode);
    }

    [Fact]
    public async Task Token_of_another_type_is_rejected()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        const string token = "token-de-redefinicao-de-senha";
        await ExecuteAsync($"""
            INSERT INTO identity.email_tokens (external_id, purpose, email, token_hash, expires_at, created_at, updated_at)
            VALUES (uuidv7(), 'PasswordReset', '{Email}', decode('{HashHex(token)}', 'hex'), now() + interval '1 hour', now(), now())
            """);

        await AssertInvalidTokenAsync(await CompleteAsync(client, token, Password));
        Assert.Equal(0L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.users"));
    }

    [Theory]
    [InlineData("token-que-nao-existe")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Unknown_or_empty_token_is_rejected(string? token)
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();

        await AssertInvalidTokenAsync(await CompleteAsync(client, token, Password));
    }

    [Theory]
    [InlineData("curta demais")]
    [InlineData("passwordpassword")]
    [InlineData("eu sou o novo@example.com mesmo")]
    [InlineData(null)]
    public async Task Password_outside_the_policy_returns_400_and_keeps_the_token(string? password)
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        var token = await RequestTokenAsync(client);

        var response = await CompleteAsync(client, token, password);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(PasswordRejectedType, body.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("detail").GetString()));
        Assert.Null(await QueryScalarAsync<DateTime?>("SELECT used_at FROM identity.email_tokens"));
        Assert.Equal(HttpStatusCode.NoContent, (await CompleteAsync(client, token, Password)).StatusCode);
    }

    [Fact]
    public async Task Concurrent_completions_create_a_single_account()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        var token = await RequestTokenAsync(client);

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => CompleteAsync(client, token, Password)));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.NoContent);
        Assert.All(responses.Where(response => response.StatusCode != HttpStatusCode.NoContent), response => Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode));
        Assert.Equal(1L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.users"));
    }

    [Fact]
    public async Task Email_taken_after_the_request_creates_no_account()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        var token = await RequestTokenAsync(client);
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            context.Users.Add(User.Create(Email, "hash", UserRole.Customer));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await AssertInvalidTokenAsync(await CompleteAsync(client, token, Password));
        Assert.Equal(1L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.users"));
        Assert.Equal("hash", await QueryScalarAsync<string>("SELECT password_hash FROM identity.users"));
    }

    private ApiFactory CreateApi() => Api.WithWebHostBuilder(builder =>
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IEmailSender>(_emails);
            services.AddSingleton<TimeProvider>(_time);
            services.AddSingleton<IPasswordHasher>(new AspNetCorePasswordHasher(iterationCount: 1_000));
        }));

    private async Task<string> RequestTokenAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(RequestRoute, new { Email }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return _emails.TokenSentTo(Email);
    }

    private static Task<HttpResponseMessage> CompleteAsync(HttpClient client, string? token, string? password) =>
        client.PostAsJsonAsync(CompleteRoute, new { Token = token, Password = password }, TestContext.Current.CancellationToken);

    private static async Task AssertInvalidTokenAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(InvalidTokenType, body.GetProperty("type").GetString());
        Assert.Equal("Link de cadastro inválido ou expirado.", body.GetProperty("title").GetString());
    }

    private static string HashHex(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
