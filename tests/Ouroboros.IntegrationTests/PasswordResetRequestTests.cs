using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Ouroboros.Identity.Application.Notifications;
using Ouroboros.Identity.Domain.Users;
using Ouroboros.Identity.Infrastructure.Persistence;

namespace Ouroboros.IntegrationTests;

// Solicitação de redefinição de senha, contra o PostgreSQL real. Os e-mails são capturados.
public sealed class PasswordResetRequestTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private const string Route = "/api/v1/identity/password-resets";
    private const string Email = "cliente@example.com";

    private readonly CapturingEmailSender _emails = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly ConcurrentQueue<string> _logs = new();

    [Fact]
    public async Task Active_customer_gets_202_and_a_link()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api, Email, UserRole.Customer);

        var response = await RequestAsync(client, "  Cliente@Example.com ");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var message = Assert.Single(_emails.Messages);
        Assert.Equal(Email, message.To);
        Assert.Contains("http://localhost:3000/senha/redefinir?token=", message.Body);
        var token = _emails.TokenSentTo(Email);
        Assert.Equal(1L, await QueryScalarAsync<long>(
            $"SELECT count(*) FROM identity.email_tokens t JOIN identity.users u ON u.id = t.user_id WHERE u.email = '{Email}' AND t.purpose = 'PasswordReset' AND t.token_hash = decode('{HashHex(token)}', 'hex')"));
        Assert.Equal(_time.GetUtcNow().AddHours(1).UtcDateTime, await QueryScalarAsync<DateTime>("SELECT expires_at FROM identity.email_tokens"), TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Unknown_email_gets_the_same_202_and_nothing_is_sent()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api, Email, UserRole.Customer);

        var known = await RequestAsync(client, Email);
        var unknown = await RequestAsync(client, "ninguem@example.com");

        Assert.Equal(HttpStatusCode.Accepted, unknown.StatusCode);
        Assert.Equal(await known.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), await unknown.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Single(_emails.Messages);
        Assert.Equal(1L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.email_tokens"));
    }

    [Fact]
    public async Task Inactive_account_gets_no_link()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api, Email, UserRole.Customer);
        await ExecuteAsync($"UPDATE identity.users SET is_active = false WHERE email = '{Email}'");

        Assert.Equal(HttpStatusCode.Accepted, (await RequestAsync(client, Email)).StatusCode);
        Assert.Empty(_emails.Messages);
    }

    // Contas de backoffice não são recuperadas pela API pública.
    [Fact]
    public async Task Admin_account_gets_no_link()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api, "oi@adnerscarpelini.com", UserRole.Admin);

        Assert.Equal(HttpStatusCode.Accepted, (await RequestAsync(client, "oi@adnerscarpelini.com")).StatusCode);
        Assert.Empty(_emails.Messages);
        Assert.Equal(0L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.email_tokens"));
    }

    [Fact]
    public async Task New_request_invalidates_the_previous_token_and_keeps_the_password()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api, Email, UserRole.Customer);

        await RequestAsync(client, Email);
        var first = _emails.TokenSentTo(Email);
        await RequestAsync(client, Email);
        var second = _emails.TokenSentTo(Email);

        Assert.NotNull(await QueryScalarAsync<DateTime?>($"SELECT revoked_at FROM identity.email_tokens WHERE token_hash = decode('{HashHex(first)}', 'hex')"));
        Assert.Null(await QueryScalarAsync<DateTime?>($"SELECT revoked_at FROM identity.email_tokens WHERE token_hash = decode('{HashHex(second)}', 'hex')"));
        Assert.Equal("hash", await QueryScalarAsync<string>($"SELECT password_hash FROM identity.users WHERE email = '{Email}'"));
    }

    [Fact]
    public async Task Concurrent_requests_leave_a_single_pending_token()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api, Email, UserRole.Customer);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => RequestAsync(client, Email)));

        Assert.Equal(1L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.email_tokens WHERE revoked_at IS NULL"));
    }

    // Um endereço equivalente ao da conta não recebe o link: ele só vai para o e-mail gravado na conta.
    [Theory]
    [InlineData("\"cliente\"@example.com")]
    [InlineData("cliente@example.com.")]
    [InlineData("Cliente <cliente@example.com>")]
    [InlineData("nao-e-email")]
    public async Task Invalid_email_returns_400(string email)
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api, Email, UserRole.Customer);

        var response = await RequestAsync(client, email);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_emails.Messages);
    }

    [Fact]
    public async Task Above_the_limit_returns_429()
    {
        await using var api = CreateApi().WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(configuration =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Identity:RateLimiting:Policies:identity-password-reset:PermitLimit"] = "1",
                ["Identity:RateLimiting:Policies:identity-password-reset:Window"] = "01:00:00",
            })));
        using var client = api.CreateClient();

        Assert.Equal(HttpStatusCode.Accepted, (await RequestAsync(client, Email)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await RequestAsync(client, Email)).StatusCode);
    }

    [Fact]
    public async Task Token_never_appears_in_the_response_or_the_logs()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api, Email, UserRole.Customer);

        var response = await RequestAsync(client, Email);

        var token = _emails.TokenSentTo(Email);
        Assert.DoesNotContain(token, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.NotEmpty(_logs);
        Assert.DoesNotContain(_logs, entry => entry.Contains(token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Email_failure_keeps_the_same_202()
    {
        _emails.Fail = true;
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api, Email, UserRole.Customer);

        Assert.Equal(HttpStatusCode.Accepted, (await RequestAsync(client, Email)).StatusCode);
        Assert.Contains(_logs, entry => entry.Contains("Falha ao enviar o e-mail de redefinição de senha", StringComparison.Ordinal));
    }

    private ApiFactory CreateApi() => Api.WithWebHostBuilder(builder =>
    {
        builder.ConfigureLogging(logging => logging.AddProvider(new ListLoggerProvider(_logs)).SetMinimumLevel(LogLevel.Trace));
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IEmailSender>(_emails);
            services.AddSingleton<TimeProvider>(_time);
        });
    });

    private static async Task CreateUserAsync(ApiFactory api, string email, UserRole role)
    {
        await using var scope = api.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        context.Users.Add(User.Create(email, "hash", role));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static Task<HttpResponseMessage> RequestAsync(HttpClient client, string email) =>
        client.PostAsJsonAsync(Route, new { Email = email }, TestContext.Current.CancellationToken);

    private static string HashHex(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
