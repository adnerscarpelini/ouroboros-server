using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Ouroboros.Modules.Identity.Application.Security;
using Ouroboros.Modules.Identity.Domain.Users;
using Ouroboros.Modules.Identity.Infrastructure.Persistence;
using Ouroboros.Modules.Identity.Infrastructure.Security;

namespace Ouroboros.IntegrationTests;

// Login por e-mail e senha nos dois hosts, contra o PostgreSQL real.
public sealed class LoginTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private const string CustomerRoute = "/api/v1/identity/sessions";
    private const string BackofficeRoute = "/api/v1/admin/identity/sessions";
    private const string CustomerEmail = "cliente@example.com";
    private const string AdminEmail = "oi@adnerscarpelini.com";
    private const string Password = "uma frase longa e secreta do cliente";
    private const string InvalidCredentialsType =
        "https://github.com/adnerscarpelini/ouroboros-server/blob/main/docs/errors/identity.md#invalid-credentials";

    [Fact]
    public async Task Customer_login_returns_tokens_with_identity_claims()
    {
        var userId = await CreateUserAsync(CustomerEmail, UserRole.Customer);

        var response = await Client.PostAsJsonAsync(CustomerRoute, new { Email = CustomerEmail, Password }, Cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await ReadJsonAsync(response);
        Assert.Equal("Bearer", body.GetProperty("tokenType").GetString());
        Assert.True(body.GetProperty("refreshTokenExpiresAt").GetDateTimeOffset() > body.GetProperty("accessTokenExpiresAt").GetDateTimeOffset());

        var jwt = new JsonWebToken(body.GetProperty("accessToken").GetString());
        Assert.Equal(userId.ToString(), jwt.Subject);
        Assert.Equal("Customer", jwt.GetClaim("role").Value);
        Assert.Equal(["ouroboros-api"], jwt.Audiences);
        Assert.Equal(jwt.ValidTo, body.GetProperty("accessTokenExpiresAt").GetDateTimeOffset().UtcDateTime, TimeSpan.FromSeconds(1));

        var sessionId = Guid.Parse(jwt.GetClaim("sid").Value);
        Assert.Equal(1L, await QueryScalarAsync<long>(
            $"SELECT count(*) FROM identity.sessions s JOIN identity.users u ON u.id = s.user_id WHERE s.external_id = '{sessionId}' AND u.external_id = '{userId}'"));
    }

    [Fact]
    public async Task Issued_access_token_is_accepted_by_the_same_host()
    {
        await CreateUserAsync(CustomerEmail, UserRole.Customer);
        var accessToken = (await LoginAsync(Client, CustomerRoute, CustomerEmail, Password)).GetProperty("accessToken").GetString();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/integration-probe/claims");
        request.Headers.Authorization = new("Bearer", accessToken);
        var response = await Client.SendAsync(request, Cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Email_with_different_case_and_spaces_logs_in()
    {
        await CreateUserAsync(CustomerEmail, UserRole.Customer);

        var response = await Client.PostAsJsonAsync(CustomerRoute, new { Email = "  Cliente@EXAMPLE.com ", Password }, Cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Wrong_password_returns_401_and_opens_no_session()
    {
        await CreateUserAsync(CustomerEmail, UserRole.Customer);

        var response = await Client.PostAsJsonAsync(CustomerRoute, new { Email = CustomerEmail, Password = Password + "x" }, Cancellation);

        await AssertInvalidCredentialsAsync(response);
        Assert.Equal(0L, await CountSessionsAsync());
    }

    [Fact]
    public async Task Unknown_email_returns_the_same_response_as_wrong_password()
    {
        await CreateUserAsync(CustomerEmail, UserRole.Customer);

        var wrongPassword = await Client.PostAsJsonAsync(CustomerRoute, new { Email = CustomerEmail, Password = "senha errada qualquer" }, Cancellation);
        var unknownEmail = await Client.PostAsJsonAsync(CustomerRoute, new { Email = "ninguem@example.com", Password }, Cancellation);

        Assert.Equal(await ProblemShapeAsync(wrongPassword), await ProblemShapeAsync(unknownEmail));
        Assert.Equal(0L, await CountSessionsAsync());
    }

    [Fact]
    public async Task Unknown_email_still_runs_a_password_hash_verification()
    {
        var hasher = new CountingPasswordHasher();
        await using var api = Api.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<IPasswordHasher>(hasher)));
        using var client = api.CreateClient();

        var response = await client.PostAsJsonAsync(CustomerRoute, new { Email = "ninguem@example.com", Password }, Cancellation);

        await AssertInvalidCredentialsAsync(response);
        Assert.Equal(1, hasher.Verifications);
    }

    [Fact]
    public async Task Inactive_user_with_correct_password_gets_the_same_401()
    {
        await CreateUserAsync(CustomerEmail, UserRole.Customer);
        await ExecuteAsync($"UPDATE identity.users SET is_active = false WHERE email = '{CustomerEmail}'");

        var response = await Client.PostAsJsonAsync(CustomerRoute, new { Email = CustomerEmail, Password }, Cancellation);

        await AssertInvalidCredentialsAsync(response);
        Assert.Equal(0L, await CountSessionsAsync());
    }

    [Fact]
    public async Task New_login_keeps_previous_session()
    {
        await CreateUserAsync(CustomerEmail, UserRole.Customer);

        var first = await LoginAsync(Client, CustomerRoute, CustomerEmail, Password);
        var second = await LoginAsync(Client, CustomerRoute, CustomerEmail, Password);

        Assert.NotEqual(SessionIdOf(first), SessionIdOf(second));
        Assert.Equal(2L, await CountSessionsAsync());
        Assert.Equal(1L, await CountRefreshTokensWithHashAsync(first.GetProperty("refreshToken").GetString()!));
        Assert.Equal(1L, await CountRefreshTokensWithHashAsync(second.GetProperty("refreshToken").GetString()!));
    }

    [Fact]
    public async Task Admin_cannot_log_in_through_the_external_api()
    {
        await CreateUserAsync(AdminEmail, UserRole.Admin);

        var response = await Client.PostAsJsonAsync(CustomerRoute, new { Email = AdminEmail, Password }, Cancellation);

        await AssertInvalidCredentialsAsync(response);
        Assert.Equal(0L, await CountSessionsAsync());
    }

    [Fact]
    public async Task Customer_cannot_log_in_through_the_backoffice()
    {
        await CreateUserAsync(CustomerEmail, UserRole.Customer);
        await using var backoffice = new BackofficeIntegrationApiFactory(ConnectionString);
        using var client = backoffice.CreateClient();

        var response = await client.PostAsJsonAsync(BackofficeRoute, new { Email = CustomerEmail, Password }, Cancellation);

        await AssertInvalidCredentialsAsync(response);
        Assert.Equal(0L, await CountSessionsAsync());
    }

    [Fact]
    public async Task Admin_login_through_the_backoffice_uses_backoffice_audience()
    {
        var adminId = await CreateUserAsync(AdminEmail, UserRole.Admin);
        await using var backoffice = new BackofficeIntegrationApiFactory(ConnectionString);
        using var client = backoffice.CreateClient();

        var body = await LoginAsync(client, BackofficeRoute, AdminEmail, Password);

        var jwt = new JsonWebToken(body.GetProperty("accessToken").GetString());
        Assert.Equal(["ouroboros-backoffice"], jwt.Audiences);
        Assert.Equal(adminId.ToString(), jwt.Subject);
        Assert.Equal("Admin", jwt.GetClaim("role").Value);
    }

    [Fact]
    public async Task Database_stores_only_the_sha256_of_a_32_byte_refresh_token()
    {
        await CreateUserAsync(CustomerEmail, UserRole.Customer);

        var refreshToken = (await LoginAsync(Client, CustomerRoute, CustomerEmail, Password)).GetProperty("refreshToken").GetString()!;

        Assert.Equal(32, Base64Url.DecodeFromChars(refreshToken).Length);
        var stored = await QueryScalarAsync<byte[]>("SELECT token_hash FROM identity.refresh_tokens");
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)), stored);
    }

    [Fact]
    public async Task Login_saves_the_rehashed_password_when_parameters_are_outdated()
    {
        var oldHash = new AspNetCorePasswordHasher(iterationCount: 1_000).Hash(Password);
        await CreateUserAsync(CustomerEmail, UserRole.Customer, oldHash);

        await LoginAsync(Client, CustomerRoute, CustomerEmail, Password);

        var newHash = await QueryScalarAsync<string>($"SELECT password_hash FROM identity.users WHERE email = '{CustomerEmail}'");
        Assert.NotEqual(oldHash, newHash);
        Assert.Equal(PasswordVerificationOutcome.Succeeded, new AspNetCorePasswordHasher().Verify(newHash!, Password));
    }

    [Theory]
    [InlineData("""{"password":"uma frase longa e secreta do cliente"}""")]
    [InlineData("""{"email":"cliente@example.com"}""")]
    public async Task Missing_fields_return_400(string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await Client.PostAsync(CustomerRoute, content, Cancellation);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Logs_never_contain_the_password_or_the_tokens()
    {
        await CreateUserAsync(CustomerEmail, UserRole.Customer);
        var logs = new ConcurrentQueue<string>();
        await using var api = Api.WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging => logging.AddProvider(new ListLoggerProvider(logs)).SetMinimumLevel(LogLevel.Trace)));
        using var client = api.CreateClient();

        var body = await LoginAsync(client, CustomerRoute, CustomerEmail, Password);
        await client.PostAsJsonAsync(CustomerRoute, new { Email = CustomerEmail, Password = Password + "x" }, Cancellation);

        Assert.NotEmpty(logs);
        string[] secrets = [Password, body.GetProperty("accessToken").GetString()!, body.GetProperty("refreshToken").GetString()!];
        Assert.All(secrets, secret => Assert.DoesNotContain(logs, entry => entry.Contains(secret, StringComparison.Ordinal)));
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> LoginAsync(HttpClient client, string route, string email, string password)
    {
        var response = await client.PostAsJsonAsync(route, new { Email = email, Password = password }, Cancellation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Cancellation);

    private static Guid SessionIdOf(JsonElement body) =>
        Guid.Parse(new JsonWebToken(body.GetProperty("accessToken").GetString()).GetClaim("sid").Value);

    private static async Task AssertInvalidCredentialsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await ReadJsonAsync(response);
        Assert.Equal(InvalidCredentialsType, body.GetProperty("type").GetString());
        Assert.False(body.TryGetProperty("accessToken", out _));
        Assert.True(body.TryGetProperty("traceId", out _));
    }

    // Tudo da resposta de erro, exceto o traceId, que muda a cada requisição.
    private static async Task<string> ProblemShapeAsync(HttpResponseMessage response)
    {
        var body = await ReadJsonAsync(response);
        var fields = body.EnumerateObject().Where(property => property.Name != "traceId").Select(property => $"{property.Name}={property.Value}");
        return $"{(int)response.StatusCode} {response.Content.Headers.ContentType} {string.Join(';', fields)}";
    }

    private async Task<Guid> CreateUserAsync(string email, UserRole role, string? passwordHash = null)
    {
        await using var scope = Api.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var user = User.Create(email, passwordHash ?? scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(Password), role);
        context.Users.Add(user);
        await context.SaveChangesAsync(Cancellation);
        return user.ExternalId;
    }

    private Task<long> CountSessionsAsync() => QueryScalarAsync<long>("SELECT count(*) FROM identity.sessions");

    private Task<long> CountRefreshTokensWithHashAsync(string refreshToken) =>
        QueryScalarAsync<long>(
            $"SELECT count(*) FROM identity.refresh_tokens WHERE token_hash = decode('{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)))}', 'hex')");

    // Conta as verificações de senha, com poucas iterações para o teste ser rápido.
    private sealed class CountingPasswordHasher : IPasswordHasher
    {
        private readonly AspNetCorePasswordHasher _inner = new(iterationCount: 1_000);
        private int _verifications;

        public int Verifications => _verifications;

        public string Hash(string password) => _inner.Hash(password);

        public PasswordVerificationOutcome Verify(string passwordHash, string providedPassword)
        {
            Interlocked.Increment(ref _verifications);
            return _inner.Verify(passwordHash, providedPassword);
        }
    }
}
