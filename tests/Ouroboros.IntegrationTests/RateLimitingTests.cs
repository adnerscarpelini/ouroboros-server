using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ouroboros.Identity.Infrastructure.RateLimiting;

namespace Ouroboros.IntegrationTests;

// Limite de requisições por IP dos endpoints anônimos de autenticação. O IP de cada requisição vem
// do header X-Test-Client-IP, aplicado por um startup filter só destes testes.
public sealed class RateLimitingTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private const string LoginRoute = "/api/v1/identity/sessions";
    private const string BackofficeLoginRoute = "/api/v1/admin/identity/sessions";
    private const string ProbeRoute = "/api/v1/integration-probe/rate-limited";

    private readonly ConcurrentQueue<string> _logs = new();

    [Fact]
    public async Task Login_above_the_limit_returns_429()
    {
        await using var api = CreateApi(loginLimit: 3);
        using var client = api.CreateClient();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, LoginRoute, "10.0.0.1")).StatusCode);
        }

        var rejected = await LoginAsync(client, LoginRoute, "10.0.0.1");

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
        Assert.InRange(rejected.Headers.RetryAfter?.Delta?.TotalSeconds ?? 0, 1, 3600);
        var body = await rejected.Content.ReadFromJsonAsync<Dictionary<string, object>>(TestContext.Current.CancellationToken);
        Assert.Equal("https://www.rfc-editor.org/rfc/rfc6585#section-4", body?["type"]?.ToString());
        Assert.True(body?.ContainsKey("traceId"));
    }

    [Fact]
    public async Task Rejection_is_logged_with_path_and_ip()
    {
        await using var api = CreateApi(loginLimit: 1);
        using var client = api.CreateClient();

        await LoginAsync(client, LoginRoute, "10.0.0.7");
        await LoginAsync(client, LoginRoute, "10.0.0.7");

        Assert.Contains(_logs, entry => entry.Contains(LoginRoute, StringComparison.Ordinal) && entry.Contains("10.0.0.7", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Other_ip_is_not_affected()
    {
        await using var api = CreateApi(loginLimit: 1);
        using var client = api.CreateClient();

        await LoginAsync(client, LoginRoute, "10.0.0.1");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await LoginAsync(client, LoginRoute, "10.0.0.1")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, LoginRoute, "10.0.0.2")).StatusCode);
    }

    [Fact]
    public async Task Each_policy_has_its_own_counter()
    {
        await using var api = CreateApi(loginLimit: 1);
        using var client = api.CreateClient();

        await LoginAsync(client, LoginRoute, "10.0.0.1");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await LoginAsync(client, LoginRoute, "10.0.0.1")).StatusCode);

        using var probe = new HttpRequestMessage(HttpMethod.Get, ProbeRoute);
        probe.Headers.Add(TestClientAddressStartupFilter.Header, "10.0.0.1");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(probe, TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Refresh_has_its_own_policy()
    {
        await using var api = CreateApi(loginLimit: 100).WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(configuration =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Identity:RateLimiting:Policies:identity-refresh:PermitLimit"] = "1",
                ["Identity:RateLimiting:Policies:identity-refresh:Window"] = "01:00:00",
            })));
        using var client = api.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(client, "10.0.0.1")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await RefreshAsync(client, "10.0.0.1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, LoginRoute, "10.0.0.1")).StatusCode);
    }

    [Fact]
    public async Task Backoffice_login_is_limited_too()
    {
        await using var backoffice = new BackofficeIntegrationApiFactory(ConnectionString)
            .WithWebHostBuilder(builder => Configure(builder, loginLimit: 1));
        using var client = backoffice.CreateClient();

        await LoginAsync(client, BackofficeLoginRoute, "10.0.0.1");

        Assert.Equal(HttpStatusCode.TooManyRequests, (await LoginAsync(client, BackofficeLoginRoute, "10.0.0.1")).StatusCode);
    }

    [Theory]
    [InlineData("PermitLimit", "0")]
    [InlineData("PermitLimit", "-1")]
    [InlineData("Window", "00:00:00")]
    [InlineData("Window", "-00:01:00")]
    public void Non_positive_limit_or_window_fails_on_startup(string field, string value)
    {
        using var api = Api.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(configuration =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"Identity:RateLimiting:Policies:identity-login:{field}"] = value,
            })));

        var error = Assert.Throws<OptionsValidationException>(() => api.CreateClient());

        Assert.Contains($"Identity:RateLimiting:Policies:identity-login:{field}", error.Message);
    }

    [Theory]
    [InlineData("PermitLimit", "abc")]
    [InlineData("Window", "xyz")]
    public void Value_in_invalid_format_is_reported_on_startup(string field, string value)
    {
        using var api = Api.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(configuration =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"Identity:RateLimiting:Policies:identity-login:{field}"] = value,
            })));

        var error = Assert.Throws<OptionsValidationException>(() => api.CreateClient());

        Assert.Contains($"Identity:RateLimiting:Policies:identity-login:{field}' tem o valor inválido '{value}'", error.Message);
    }

    [Fact]
    public void Missing_login_policy_is_rejected()
    {
        var result = new AuthRateLimitOptionsValidator(new ConfigurationBuilder().Build()).Validate(null, new AuthRateLimitOptions());

        Assert.True(result.Failed);
        Assert.Contains("Identity:RateLimiting:Policies:identity-login", result.FailureMessage);
    }

    private ApiFactory CreateApi(int loginLimit) => Api.WithWebHostBuilder(builder => Configure(builder, loginLimit));

    private void Configure(IWebHostBuilder builder, int loginLimit)
    {
        builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Identity:RateLimiting:Policies:identity-login:PermitLimit"] = loginLimit.ToString(CultureInfo.InvariantCulture),
            ["Identity:RateLimiting:Policies:identity-login:Window"] = "01:00:00",
            [$"Identity:RateLimiting:Policies:{RateLimitedProbeController.Policy}:PermitLimit"] = "100",
            [$"Identity:RateLimiting:Policies:{RateLimitedProbeController.Policy}:Window"] = "01:00:00",
        }));
        builder.ConfigureLogging(logging => logging.AddProvider(new ListLoggerProvider(_logs)));
        builder.ConfigureTestServices(services => services.AddTransient<IStartupFilter, TestClientAddressStartupFilter>());
    }

    private static async Task<HttpResponseMessage> LoginAsync(HttpClient client, string route, string clientAddress)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = JsonContent.Create(new { Email = "ninguem@example.com", Password = "uma senha qualquer bem longa" }),
        };
        request.Headers.Add(TestClientAddressStartupFilter.Header, clientAddress);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<HttpResponseMessage> RefreshAsync(HttpClient client, string clientAddress)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/identity/sessions/refresh")
        {
            Content = JsonContent.Create(new { RefreshToken = "token-que-nao-existe" }),
        };
        request.Headers.Add(TestClientAddressStartupFilter.Header, clientAddress);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private sealed class TestClientAddressStartupFilter : IStartupFilter
    {
        public const string Header = "X-Test-Client-IP";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (IPAddress.TryParse(context.Request.Headers[Header], out var address))
                {
                    context.Connection.RemoteIpAddress = address;
                }

                return nextMiddleware(context);
            });
            next(app);
        };
    }
}

// Endpoint anônimo com uma segunda política, para provar que cada política tem o próprio contador.
[ApiController]
[AllowAnonymous]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/integration-probe/rate-limited")]
public sealed class RateLimitedProbeController : ControllerBase
{
    public const string Policy = "integration-probe";

    [HttpGet]
    [EnableRateLimiting(Policy)]
    public IActionResult Get() => Ok();
}
