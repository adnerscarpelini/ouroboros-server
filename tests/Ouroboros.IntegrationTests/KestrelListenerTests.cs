extern alias identityapi;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using identityapi::Ouroboros.Identity.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Ouroboros.Identity.Application.Security;
using Ouroboros.Identity.Domain.Users;
using Ouroboros.Identity.Infrastructure.Persistence;

namespace Ouroboros.IntegrationTests;

// A API de Identity em um Kestrel real, com um listener por público em portas livres de loopback: a separação
// vem da conexão que aceitou a requisição, não de cabeçalho. Complementa os testes com TestServer.
public sealed class KestrelListenerTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private const string Password = "uma frase longa e secreta do usuário";
    private const string CustomerEmail = "cliente@example.com";
    private const string AdminEmail = "oi@adnerscarpelini.com";

    [Fact]
    public async Task Each_listener_serves_only_its_own_public_with_its_own_audience()
    {
        var (publicPort, adminPort) = (FreePort(), FreePort());
        await using var host = await StartAsync(publicPort, adminPort);
        await CreateUsersAsync(host.App);
        using var publicClient = ClientFor(publicPort);
        using var adminClient = ClientFor(adminPort);

        var customerToken = await LoginAsync(publicClient, "/api/v1/identity/sessions", CustomerEmail);
        var adminToken = await LoginAsync(adminClient, "/api/v1/admin/identity/sessions", AdminEmail);

        // Cada token funciona no listener do próprio público.
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(publicClient, "/api/v1/identity/me", customerToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(adminClient, "/api/v1/admin/identity/me", adminToken)).StatusCode);

        // O token de um canal é recusado no outro com o 401 genérico.
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(adminClient, "/api/v1/admin/identity/me", customerToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(publicClient, "/api/v1/identity/me", adminToken)).StatusCode);

        // A rota do outro público responde exatamente como uma rota inexistente, com qualquer token.
        foreach (var token in new[] { customerToken, adminToken })
        {
            Assert.Equal(
                (await GetAsync(publicClient, "/api/v1/admin/identity/nao-existe", token)).StatusCode,
                (await GetAsync(publicClient, "/api/v1/admin/identity/me", token)).StatusCode);
            Assert.Equal(
                (await GetAsync(adminClient, "/api/v1/identity/nao-existe", token)).StatusCode,
                (await GetAsync(adminClient, "/api/v1/identity/me", token)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(publicClient, "/api/v1/admin/identity/me", customerToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(adminClient, "/api/v1/identity/me", adminToken)).StatusCode);

        // Anônimo: o login do outro público responde como qualquer rota inexistente, sem o tipo de erro do login.
        var adminLoginOnPublic = await PostLoginAsync(publicClient, "/api/v1/admin/identity/sessions", AdminEmail);
        var nonexistent = await PostLoginAsync(publicClient, "/api/v1/admin/identity/nao-existe", AdminEmail);
        Assert.Equal(nonexistent.StatusCode, adminLoginOnPublic.StatusCode);
        Assert.Equal(await nonexistent.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ContinueWith(task => Strip(task.Result)), Strip(await adminLoginOnPublic.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
        var customerLoginOnAdmin = await PostLoginAsync(adminClient, "/api/v1/identity/sessions", CustomerEmail);
        Assert.NotEqual(HttpStatusCode.OK, customerLoginOnAdmin.StatusCode);
    }

    [Fact]
    public async Task Admin_listener_is_not_reachable_through_a_non_loopback_address_by_default()
    {
        var (publicPort, adminPort) = (FreePort(), FreePort());
        await using var host = await StartAsync(publicPort, adminPort);
        var machineAddress = NonLoopbackAddress();
        Assert.SkipWhen(machineAddress is null, "A máquina não tem endereço IPv4 fora de loopback.");

        using var viaLoopback = ClientFor(adminPort);
        Assert.NotEqual(HttpStatusCode.OK, (await viaLoopback.GetAsync("/api/v1/admin/identity/me", TestContext.Current.CancellationToken)).StatusCode);

        using var viaMachine = new HttpClient { BaseAddress = new Uri($"http://{machineAddress}:{adminPort}"), Timeout = TimeSpan.FromSeconds(5) };
        await Assert.ThrowsAsync<HttpRequestException>(() => viaMachine.GetAsync("/api/v1/admin/identity/me", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Exposing_the_admin_listener_outside_loopback_requires_an_explicit_setting()
    {
        var (publicPort, adminPort) = (FreePort(), FreePort());

        var error = await Assert.ThrowsAsync<OptionsValidationException>(() =>
            StartAsync(publicPort, adminPort, adminUrl: $"http://0.0.0.0:{adminPort}"));
        Assert.Contains("AllowNonLoopback", error.Message, StringComparison.Ordinal);

        await using var exposed = await StartAsync(publicPort, adminPort, adminUrl: $"http://0.0.0.0:{adminPort}", allowNonLoopback: true);
        using var client = ClientFor(adminPort);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/admin/identity/me", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Theory]
    [InlineData("http://nome-de-host.example:5142")]
    [InlineData("ftp://localhost:5142")]
    [InlineData("localhost")]
    public async Task Invalid_listener_urls_prevent_startup(string adminUrl)
    {
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => StartAsync(FreePort(), FreePort(), adminUrl: adminUrl));

        Assert.Contains("Identity:Listeners:Admin:Urls", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Listeners_cannot_share_a_port()
    {
        var port = FreePort();

        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => StartAsync(port, port));

        Assert.Contains("ao mesmo tempo", error.Message, StringComparison.Ordinal);
    }

    private async Task<RunningHost> StartAsync(int publicPort, int adminPort, string? adminUrl = null, bool allowNonLoopback = false)
    {
        var app = IdentityApiHost.Build(
            ["--environment", "Testing", "--contentRoot", RepositoryRoot.Resolve("src", "Services", "Identity", "Ouroboros.Identity.Api")],
            builder => builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Ouroboros"] = ConnectionString,
                ["Authentication:Jwt:SigningKey"] = IntegrationApiFactory.SigningKey,
                ["Identity:Listeners:Public:Urls:0"] = $"http://127.0.0.1:{publicPort}",
                ["Identity:Listeners:Admin:Urls:0"] = adminUrl ?? $"http://127.0.0.1:{adminPort}",
                ["Identity:Listeners:Admin:AllowNonLoopback"] = allowNonLoopback.ToString(),
                ["Identity:RateLimiting:Policies:identity-login:PermitLimit"] = "100000",
            }));
        app.UsePipeline();
        try
        {
            await app.StartAsync(TestContext.Current.CancellationToken);
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }

        return new RunningHost(app);
    }

    private static async Task CreateUsersAsync(WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var hash = scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(Password);
        context.Users.Add(User.Create(CustomerEmail, hash, UserRole.Customer));
        context.Users.Add(User.Create(AdminEmail, hash, UserRole.Admin));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static HttpClient ClientFor(int port) => new() { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

    private static async Task<string> LoginAsync(HttpClient client, string route, string email)
    {
        using var response = await PostLoginAsync(client, route, email);
        Assert.True(
            response.IsSuccessStatusCode,
            $"{route}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return body.GetProperty("accessToken").GetString()!;
    }

    private static Task<HttpResponseMessage> PostLoginAsync(HttpClient client, string route, string email) =>
        client.PostAsJsonAsync(route, new { Email = email, Password }, TestContext.Current.CancellationToken);

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string route, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    // O traceId muda a cada resposta; o restante do corpo é o que um cliente compara.
    private static string Strip(string body) => System.Text.RegularExpressions.Regex.Replace(body, "\"traceId\":\"[^\"]*\"", "");

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static IPAddress? NonLoopbackAddress() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(nic => nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
        .Select(address => address.Address)
        .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address));

    private sealed class RunningHost(WebApplication app) : IAsyncDisposable
    {
        public WebApplication App => app;

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
