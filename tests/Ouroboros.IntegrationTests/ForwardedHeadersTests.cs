using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ouroboros.IntegrationTests;

// Rate limit por IP com cabeçalhos X-Forwarded-*: valem só de proxies configurados (Identity:ForwardedHeaders). O IP
// da conexão vem do header de teste X-Test-Client-IP, aplicado por um startup filter só destes testes.
public sealed class ForwardedHeadersTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private const string LoginRoute = "/api/v1/identity/sessions";
    private const string AdminLoginRoute = "/api/v1/admin/identity/sessions";

    [Fact]
    public async Task Forwarded_for_from_an_unconfigured_source_is_ignored_by_the_rate_limit()
    {
        await using var api = CreateApi(settings: []);
        using var client = api.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, LoginRoute, "198.51.100.7", forwardedFor: "203.0.113.1")).StatusCode);

        // O IP forjado no cabeçalho não troca o contador: a conexão é a mesma.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await LoginAsync(client, LoginRoute, "198.51.100.7", forwardedFor: "203.0.113.2")).StatusCode);
    }

    [Fact]
    public async Task Forwarded_for_from_a_configured_proxy_identifies_the_client()
    {
        await using var api = CreateApi(settings: new() { ["Identity:ForwardedHeaders:KnownProxies:0"] = "198.51.100.7" });
        using var client = api.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, LoginRoute, "198.51.100.7", forwardedFor: "203.0.113.1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, LoginRoute, "198.51.100.7", forwardedFor: "203.0.113.2")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await LoginAsync(client, LoginRoute, "198.51.100.7", forwardedFor: "203.0.113.1")).StatusCode);
    }

    [Fact]
    public async Task Forwarded_for_from_a_source_outside_the_configured_proxies_is_ignored()
    {
        await using var api = CreateApi(settings: new() { ["Identity:ForwardedHeaders:KnownNetworks:0"] = "10.0.0.0/8" });
        using var client = api.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, LoginRoute, "198.51.100.7", forwardedFor: "203.0.113.1")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await LoginAsync(client, LoginRoute, "198.51.100.7", forwardedFor: "203.0.113.2")).StatusCode);
    }

    [Fact]
    public async Task Each_listener_keeps_its_own_login_counter()
    {
        await using var api = CreateApi(settings: []);
        using var external = api.CreateClient();
        using var admin = api.CreateClient();
        admin.DefaultRequestHeaders.Add(IntegrationApiFactory.ListenerHeader, IntegrationApiFactory.AdminListener);

        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(external, LoginRoute, "198.51.100.7")).StatusCode);

        // O mesmo IP já gastou o limite do login externo e ainda tem o do administrativo, como em dois processos.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await LoginAsync(external, LoginRoute, "198.51.100.7")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(admin, AdminLoginRoute, "198.51.100.7")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await LoginAsync(admin, AdminLoginRoute, "198.51.100.7")).StatusCode);
    }

    private ApiFactory CreateApi(Dictionary<string, string?> settings) => Api.WithWebHostBuilder(builder =>
    {
        settings["Identity:RateLimiting:Policies:identity-login:PermitLimit"] = "1";
        settings["Identity:RateLimiting:Policies:identity-login:Window"] = "01:00:00";
        builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(settings));
        builder.ConfigureTestServices(services => services.AddTransient<IStartupFilter, ClientAddressStartupFilter>());
    });

    private static async Task<HttpResponseMessage> LoginAsync(HttpClient client, string route, string connectionAddress, string? forwardedFor = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = JsonContent.Create(new { Email = "ninguem@example.com", Password = "uma senha qualquer bem longa" }),
        };
        request.Headers.Add(ClientAddressStartupFilter.Header, connectionAddress);
        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private sealed class ClientAddressStartupFilter : IStartupFilter
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
