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

// Solicitação de cadastro de cliente por e-mail, contra o PostgreSQL real. Os e-mails são capturados.
public sealed class RegistrationRequestTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private const string Route = "/api/v1/identity/registrations";
    private const string Email = "novo@example.com";

    private readonly CapturingEmailSender _emails = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly ConcurrentQueue<string> _logs = new();

    [Fact]
    public async Task Free_email_gets_202_and_a_link()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();

        var response = await RequestAsync(client, "  Novo@Example.com ");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var message = Assert.Single(_emails.Messages);
        Assert.Equal(Email, message.To);
        Assert.Contains("http://localhost:3000/cadastro/concluir?token=", message.Body);
        var token = _emails.TokenSentTo(Email);
        Assert.Equal(32, System.Buffers.Text.Base64Url.DecodeFromChars(token).Length);

        Assert.Equal(1L, await QueryScalarAsync<long>(
            $"SELECT count(*) FROM identity.email_tokens WHERE email = '{Email}' AND purpose = 'Registration' AND token_hash = decode('{HashHex(token)}', 'hex')"));
        Assert.Equal(_time.GetUtcNow().AddHours(24).UtcDateTime, await QueryScalarAsync<DateTime>("SELECT expires_at FROM identity.email_tokens"), TimeSpan.FromMilliseconds(1));
        Assert.Equal(0L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.users"));
    }

    [Fact]
    public async Task Email_of_an_existing_user_gets_the_same_202_and_nothing_is_sent()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            context.Users.Add(User.Create(Email, "hash", UserRole.Customer));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var taken = await RequestAsync(client, Email);
        var free = await RequestAsync(client, "livre@example.com");

        Assert.Equal(HttpStatusCode.Accepted, taken.StatusCode);
        Assert.Equal(await free.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), await taken.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain(_emails.Messages, message => message.To == Email);
        Assert.Equal(0L, await QueryScalarAsync<long>($"SELECT count(*) FROM identity.email_tokens WHERE email = '{Email}'"));
    }

    [Fact]
    public async Task New_request_invalidates_the_previous_token()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();

        await RequestAsync(client, Email);
        var first = _emails.TokenSentTo(Email);
        await RequestAsync(client, Email);
        var second = _emails.TokenSentTo(Email);

        Assert.NotEqual(first, second);
        Assert.NotNull(await QueryScalarAsync<DateTime?>($"SELECT revoked_at FROM identity.email_tokens WHERE token_hash = decode('{HashHex(first)}', 'hex')"));
        Assert.Null(await QueryScalarAsync<DateTime?>($"SELECT revoked_at FROM identity.email_tokens WHERE token_hash = decode('{HashHex(second)}', 'hex')"));
    }

    [Fact]
    public async Task Concurrent_requests_leave_a_single_pending_token()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => RequestAsync(client, Email)));

        Assert.Equal(8L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.email_tokens"));
        Assert.Equal(1L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.email_tokens WHERE revoked_at IS NULL"));
    }

    // Endereços equivalentes ao de uma conta (aspas, ponto final) não escapam da verificação de e-mail usado.
    [Theory]
    [InlineData("\"vitima\"@example.com")]
    [InlineData("vitima@example.com.")]
    public async Task Address_equivalent_to_an_existing_user_is_rejected(string email)
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            context.Users.Add(User.Create("vitima@example.com", "hash", UserRole.Customer));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var response = await RequestAsync(client, email);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_emails.Messages);
        Assert.Equal(0L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.email_tokens"));
    }

    [Fact]
    public async Task Role_in_the_body_is_ignored()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();

        var response = await client.PostAsJsonAsync(Route, new { Email, Role = "Admin" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(0L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.users"));
        Assert.Equal(1L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.email_tokens"));
    }

    [Theory]
    [InlineData("nao-e-email")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("\"novo\"@example.com")]
    [InlineData("novo@example.com.")]
    [InlineData("vic tim@example.com")]
    [InlineData("Novo <novo@example.com>")]
    [InlineData("novo@example.com>")]
    [InlineData("novo@localhost")]
    [InlineData("novo@[127.0.0.1]")]
    [InlineData("novo(comentario)@example.com")]
    public async Task Invalid_email_returns_400(string? email)
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();

        var response = await client.PostAsJsonAsync(Route, new { Email = email }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_emails.Messages);
        Assert.Equal(0L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.email_tokens"));
    }

    [Fact]
    public async Task Above_the_limit_returns_429()
    {
        await using var api = CreateApi().WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(configuration =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Identity:RateLimiting:Policies:identity-registration:PermitLimit"] = "1",
                ["Identity:RateLimiting:Policies:identity-registration:Window"] = "01:00:00",
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

        var response = await RequestAsync(client, Email);

        var token = _emails.TokenSentTo(Email);
        Assert.DoesNotContain(token, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.NotEmpty(_logs);
        Assert.DoesNotContain(_logs, entry => entry.Contains(token, StringComparison.Ordinal));
    }

    // Falha no envio não muda a resposta (senão revelaria que o e-mail está livre) e não leva o token ao log.
    [Fact]
    public async Task Email_failure_keeps_the_same_202()
    {
        _emails.Fail = true;
        await using var api = CreateApi();
        using var client = api.CreateClient();

        var response = await RequestAsync(client, Email);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Contains(_logs, entry => entry.Contains("Falha ao enviar o e-mail de cadastro", StringComparison.Ordinal));
    }

    private WebApplicationFactory<Program> CreateApi() => Api.WithWebHostBuilder(builder =>
    {
        builder.ConfigureLogging(logging => logging.AddProvider(new ListLoggerProvider(_logs)).SetMinimumLevel(LogLevel.Trace));
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IEmailSender>(_emails);
            services.AddSingleton<TimeProvider>(_time);
        });
    });

    private static Task<HttpResponseMessage> RequestAsync(HttpClient client, string email) =>
        client.PostAsJsonAsync(Route, new { Email = email }, TestContext.Current.CancellationToken);

    private static string HashHex(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
