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

// Redefinição de senha com o token do link, contra o PostgreSQL real.
public sealed class PasswordResetCompletionTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private const string RequestRoute = "/api/v1/identity/password-resets";
    private const string CompleteRoute = "/api/v1/identity/password-resets/complete";
    private const string LoginRoute = "/api/v1/identity/sessions";
    private const string Email = "cliente@example.com";
    private const string OldPassword = "uma frase longa e antiga do usuario";
    private const string NewPassword = "outra frase longa e bem diferente agora";
    private const string InvalidTokenType =
        "https://github.com/adnerscarpelini/ouroboros-server/blob/main/docs/errors/identity.md#invalid-password-reset-token";
    private const string PasswordRejectedType =
        "https://github.com/adnerscarpelini/ouroboros-server/blob/main/docs/errors/identity.md#password-rejected";

    private readonly CapturingEmailSender _emails = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    [Fact]
    public async Task Valid_token_replaces_the_password()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api);
        var token = await RequestTokenAsync(client);

        var response = await CompleteAsync(client, token, NewPassword);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, OldPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, NewPassword)).StatusCode);
        Assert.NotNull(await QueryScalarAsync<DateTime?>("SELECT used_at FROM identity.email_tokens"));
    }

    [Fact]
    public async Task Open_sessions_stop_renewing()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api);
        var first = await RefreshTokenOfLoginAsync(client);
        var second = await RefreshTokenOfLoginAsync(client);
        var token = await RequestTokenAsync(client);

        await CompleteAsync(client, token, NewPassword);

        Assert.Equal(0L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.refresh_tokens WHERE revoked_at IS NULL"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(client, first)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(client, second)).StatusCode);
    }

    [Fact]
    public async Task Locked_account_logs_in_right_after_the_reset()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api);
        await ExecuteAsync($"UPDATE identity.users SET locked_until = now() + interval '1 hour', failed_login_count = 3 WHERE email = '{Email}'");
        var token = await RequestTokenAsync(client);

        await CompleteAsync(client, token, NewPassword);

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, NewPassword)).StatusCode);
        Assert.Equal(0, await QueryScalarAsync<int>($"SELECT failed_login_count FROM identity.users WHERE email = '{Email}'"));
    }

    [Fact]
    public async Task Used_token_is_rejected()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api);
        var token = await RequestTokenAsync(client);
        await CompleteAsync(client, token, NewPassword);

        await AssertInvalidTokenAsync(await CompleteAsync(client, token, NewPassword + " de novo"));
    }

    [Fact]
    public async Task Expired_token_is_rejected()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api);
        var token = await RequestTokenAsync(client);
        _time.Advance(TimeSpan.FromHours(1));

        await AssertInvalidTokenAsync(await CompleteAsync(client, token, NewPassword));
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, OldPassword)).StatusCode);
    }

    [Fact]
    public async Task Token_invalidated_by_a_newer_request_is_rejected()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api);
        var first = await RequestTokenAsync(client);
        var second = await RequestTokenAsync(client);

        await AssertInvalidTokenAsync(await CompleteAsync(client, first, NewPassword));
        Assert.Equal(HttpStatusCode.NoContent, (await CompleteAsync(client, second, NewPassword)).StatusCode);
    }

    [Fact]
    public async Task Token_of_another_type_is_rejected()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api);
        const string token = "token-de-cadastro";
        await ExecuteAsync($"""
            INSERT INTO identity.email_tokens (external_id, purpose, email, token_hash, expires_at, created_at, updated_at)
            VALUES (uuidv7(), 'Registration', '{Email}', decode('{HashHex(token)}', 'hex'), now() + interval '1 hour', now(), now())
            """);

        await AssertInvalidTokenAsync(await CompleteAsync(client, token, NewPassword));
    }

    [Fact]
    public async Task Token_of_an_inactive_user_is_rejected()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api);
        var token = await RequestTokenAsync(client);
        await ExecuteAsync($"UPDATE identity.users SET is_active = false WHERE email = '{Email}'");

        await AssertInvalidTokenAsync(await CompleteAsync(client, token, NewPassword));
        Assert.Null(await QueryScalarAsync<DateTime?>("SELECT used_at FROM identity.email_tokens"));
    }

    [Theory]
    [InlineData("token-que-nao-existe")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Unknown_or_empty_token_is_rejected(string? token)
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();

        await AssertInvalidTokenAsync(await CompleteAsync(client, token, NewPassword));
    }

    [Theory]
    [InlineData("curta demais")]
    [InlineData("passwordpassword")]
    [InlineData(OldPassword)]
    [InlineData(null)]
    public async Task Password_outside_the_policy_or_equal_to_the_current_keeps_the_token(string? password)
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api);
        var token = await RequestTokenAsync(client);

        var response = await CompleteAsync(client, token, password);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(PasswordRejectedType, body.GetProperty("type").GetString());
        Assert.Null(await QueryScalarAsync<DateTime?>("SELECT used_at FROM identity.email_tokens"));
        Assert.Equal(HttpStatusCode.NoContent, (await CompleteAsync(client, token, NewPassword)).StatusCode);
    }

    [Fact]
    public async Task Concurrent_resets_with_the_same_token_change_the_password_once()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api);
        var token = await RequestTokenAsync(client);
        string[] passwords = [.. Enumerable.Range(0, 5).Select(index => $"{NewPassword} numero {index}")];

        var responses = await Task.WhenAll(passwords.Select(password => CompleteAsync(client, token, password)));

        var winner = Assert.Single(Enumerable.Range(0, 5), index => responses[index].StatusCode == HttpStatusCode.NoContent);
        Assert.All(responses.Where(response => response.StatusCode != HttpStatusCode.NoContent), response => Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode));
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, passwords[winner])).StatusCode);
    }

    // Falha antes ou depois da troca de senha deve desfazer inclusive consumo e desbloqueio.
    [Theory]
    [InlineData("identity.users", "password_hash")]
    [InlineData("identity.refresh_tokens", "revoked_at")]
    public async Task Failure_in_any_write_keeps_everything_as_it_was(string table, string column)
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api);
        var session = await RefreshTokenOfLoginAsync(client);
        var token = await RequestTokenAsync(client);
        await ExecuteAsync("UPDATE identity.users SET failed_login_count = 3, locked_until = now() + interval '1 hour'");
        var originalHash = await QueryScalarAsync<string>("SELECT password_hash FROM identity.users");
        await ExecuteAsync($"""
            CREATE FUNCTION identity.fail_update() RETURNS trigger AS $$
            BEGIN RAISE EXCEPTION 'falha simulada ao trocar a senha'; END;
            $$ LANGUAGE plpgsql;
            CREATE TRIGGER fail_update BEFORE UPDATE OF {column} ON {table}
            FOR EACH ROW EXECUTE FUNCTION identity.fail_update();
            """);

        var failed = await CompleteAsync(client, token, NewPassword);

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Null(await QueryScalarAsync<DateTime?>("SELECT used_at FROM identity.email_tokens"));
        Assert.Equal(1L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.refresh_tokens WHERE revoked_at IS NULL"));
        Assert.Equal(originalHash, await QueryScalarAsync<string>("SELECT password_hash FROM identity.users"));
        Assert.Equal(3, await QueryScalarAsync<int>("SELECT failed_login_count FROM identity.users"));
        Assert.NotNull(await QueryScalarAsync<DateTime?>("SELECT locked_until FROM identity.users"));

        await ExecuteAsync($"DROP TRIGGER fail_update ON {table}");
        await ExecuteAsync("UPDATE identity.users SET locked_until = NULL");
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, OldPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(client, session)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await CompleteAsync(client, token, NewPassword)).StatusCode);
    }

    private WebApplicationFactory<Program> CreateApi() => Api.WithWebHostBuilder(builder =>
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IEmailSender>(_emails);
            services.AddSingleton<TimeProvider>(_time);
            services.AddSingleton<IPasswordHasher>(new AspNetCorePasswordHasher(iterationCount: 1_000));
        }));

    private static async Task CreateUserAsync(WebApplicationFactory<Program> api)
    {
        await using var scope = api.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        context.Users.Add(User.Create(Email, scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(OldPassword), UserRole.Customer));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<string> RequestTokenAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(RequestRoute, new { Email }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return _emails.TokenSentTo(Email);
    }

    private static Task<HttpResponseMessage> CompleteAsync(HttpClient client, string? token, string? password) =>
        client.PostAsJsonAsync(CompleteRoute, new { Token = token, Password = password }, TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string password) =>
        client.PostAsJsonAsync(LoginRoute, new { Email, Password = password }, TestContext.Current.CancellationToken);

    private static async Task<string> RefreshTokenOfLoginAsync(HttpClient client)
    {
        var response = await LoginAsync(client, OldPassword);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return body.GetProperty("refreshToken").GetString()!;
    }

    private static Task<HttpResponseMessage> RefreshAsync(HttpClient client, string refreshToken) =>
        client.PostAsJsonAsync("/api/v1/identity/sessions/refresh", new { RefreshToken = refreshToken }, TestContext.Current.CancellationToken);

    private static async Task AssertInvalidTokenAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(InvalidTokenType, body.GetProperty("type").GetString());
        Assert.Equal("Link de redefinição de senha inválido ou expirado.", body.GetProperty("title").GetString());
    }

    private static string HashHex(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
