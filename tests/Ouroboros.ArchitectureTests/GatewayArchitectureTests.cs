extern alias admingateway;
extern alias externalgateway;

using System.Diagnostics;
using System.Net;
using System.Text.Json;
using admingateway::Ouroboros.AdminGateway.Api;
using externalgateway::Ouroboros.ExternalGateway.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Ouroboros.ArchitectureTests.Dependencies;
using Ouroboros.Hosting;
using Ouroboros.Hosting.Gateway;

namespace Ouroboros.ArchitectureTests;

public sealed class GatewayArchitectureTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public void Gateways_are_executables_outside_services_without_dbcontexts_controllers_or_service_references()
    {
        var projects = SourceProjects.Current.Projects;
        var externalGateway = Assert.Single(projects, project => project.Name == "Ouroboros.ExternalGateway.Api");
        var adminGateway = Assert.Single(projects, project => project.Name == "Ouroboros.AdminGateway.Api");

        foreach (var gateway in new[] { externalGateway, adminGateway })
        {
            Assert.Equal(ProjectKind.Executable, gateway.Kind);
            Assert.Null(gateway.Service);
            Assert.DoesNotContain(gateway.References, reference => reference.Contains("Identity", StringComparison.Ordinal));
            Assert.DoesNotContain(gateway.References, reference => reference.Contains("Notifications", StringComparison.Ordinal));
            Assert.DoesNotContain(gateway.References, reference => reference.Contains("BuildingBlocks", StringComparison.Ordinal));
        }

        foreach (var assembly in new[] { typeof(ExternalGatewayEntryPoint).Assembly, typeof(AdminGatewayEntryPoint).Assembly })
        {
            var referencedNames = assembly.GetReferencedAssemblies().Select(name => name.Name!).ToList();
            Assert.DoesNotContain(referencedNames, name => name.Contains("EntityFrameworkCore", StringComparison.Ordinal));
            Assert.DoesNotContain(referencedNames, name => name.Contains("Npgsql", StringComparison.Ordinal));
            Assert.DoesNotContain(referencedNames, name => name.Contains("Identity", StringComparison.Ordinal));
            Assert.DoesNotContain(referencedNames, name => name.Contains("Notifications", StringComparison.Ordinal));
            Assert.DoesNotContain(assembly.GetTypes(), type => typeof(ControllerBase).IsAssignableFrom(type));
        }
    }

    [Fact]
    public void Admin_gateway_rejects_non_loopback_urls_by_default_and_validates_downstream_settings()
    {
        using var nonLoopbackAdmin = new WebApplicationFactory<AdminGatewayEntryPoint>()
            .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Gateway:Urls:0"] = "http://0.0.0.0:5152",
                    ["Gateway:AllowNonLoopback"] = "false",
                })));

        var nonLoopbackError = Assert.Throws<OptionsValidationException>(() => nonLoopbackAdmin.CreateClient());
        Assert.Contains("loopback", nonLoopbackError.Message, StringComparison.OrdinalIgnoreCase);

        using var invalidDownstream = new WebApplicationFactory<ExternalGatewayEntryPoint>()
            .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Gateway:Downstream:IdentityBaseUrl"] = "not-a-valid-url",
                    ["Gateway:Downstream:Timeout"] = "00:00:00",
                })));

        var downstreamError = Assert.Throws<OptionsValidationException>(() => invalidDownstream.CreateClient());
        Assert.Contains("Gateway:Downstream:IdentityBaseUrl", downstreamError.Message, StringComparison.Ordinal);
        Assert.Contains("Gateway:Downstream:Timeout", downstreamError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task External_and_admin_gateways_isolate_route_prefixes_and_never_forward_wrong_audience_routes()
    {
        var downstreamCalls = 0;
        var stubFactory = new StubDownstreamClientFactory(_ =>
        {
            Interlocked.Increment(ref downstreamCalls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        await using var externalGateway = CreateGatewayFactory<ExternalGatewayEntryPoint>(stubFactory);
        await using var adminGateway = CreateGatewayFactory<AdminGatewayEntryPoint>(stubFactory);
        using var externalClient = externalGateway.CreateClient();
        using var adminClient = adminGateway.CreateClient();

        using var adminOnExternal = await externalClient.GetAsync("/api/v1/admin/identity/me", Cancellation);
        using var externalOnAdmin = await adminClient.GetAsync("/api/v1/identity/me", Cancellation);

        Assert.Equal(HttpStatusCode.NotFound, adminOnExternal.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, externalOnAdmin.StatusCode);
        Assert.Equal(0, downstreamCalls);
    }

    [Fact]
    public async Task Unavailable_or_slow_identity_returns_sanitized_502_or_504_problem_details_within_timeout()
    {
        const string InternalDownstreamUrl = "http://internal-identity-secret.local:5141";
        const string InternalErrorDetail = "SocketException: connection refused to internal-identity-secret.local:5141";

        var failingFactory = new StubDownstreamClientFactory(_ =>
            throw new HttpRequestException(InternalErrorDetail));

        await using var unavailableGateway = CreateGatewayFactory<ExternalGatewayEntryPoint>(
            failingFactory,
            new Dictionary<string, string?>
            {
                ["Gateway:Downstream:IdentityBaseUrl"] = InternalDownstreamUrl,
            });
        using var unavailableClient = unavailableGateway.CreateClient();

        using (var response502 = await unavailableClient.GetAsync("/api/v1/identity/keys", Cancellation))
        {
            var body = await response502.Content.ReadAsStringAsync(Cancellation);
            Assert.Equal(HttpStatusCode.BadGateway, response502.StatusCode);
            Assert.Equal("application/problem+json", response502.Content.Headers.ContentType?.MediaType);
            Assert.DoesNotContain(InternalDownstreamUrl, body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("internal-identity-secret", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(InternalErrorDetail, body, StringComparison.OrdinalIgnoreCase);
            using var json = JsonDocument.Parse(body);
            Assert.Equal(502, json.RootElement.GetProperty("status").GetInt32());
            Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("traceId").GetString()));
        }

        var slowFactory = new StubDownstreamClientFactory(async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        await using var timeoutGateway = CreateGatewayFactory<ExternalGatewayEntryPoint>(
            slowFactory,
            new Dictionary<string, string?>
            {
                ["Gateway:Downstream:IdentityBaseUrl"] = InternalDownstreamUrl,
                ["Gateway:Downstream:Timeout"] = "00:00:00.200",
            });
        using var timeoutClient = timeoutGateway.CreateClient();

        var started = Stopwatch.GetTimestamp();
        using (var response504 = await timeoutClient.GetAsync("/api/v1/identity/keys", Cancellation))
        {
            var elapsed = Stopwatch.GetElapsedTime(started);
            var body = await response504.Content.ReadAsStringAsync(Cancellation);
            Assert.Equal(HttpStatusCode.GatewayTimeout, response504.StatusCode);
            Assert.True(elapsed < TimeSpan.FromSeconds(3), $"O timeout do gateway demorou demais: {elapsed}");
            Assert.Equal("application/problem+json", response504.Content.Headers.ContentType?.MediaType);
            Assert.DoesNotContain(InternalDownstreamUrl, body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("internal-identity-secret", body, StringComparison.OrdinalIgnoreCase);
            using var json = JsonDocument.Parse(body);
            Assert.Equal(504, json.RootElement.GetProperty("status").GetInt32());
            Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("traceId").GetString()));
        }
    }

    [Theory]
    [InlineData(false, "/api/v1/identity/keys")]
    [InlineData(true, "/api/v1/admin/identity/me")]
    public async Task Gateways_remove_connection_nominated_headers_in_both_directions(bool admin, string path)
    {
        var downstreamCalls = 0;
        using var stub = new StubDownstreamClientFactory(request =>
        {
            downstreamCalls++;
            Assert.False(request.Headers.Contains("Connection"));
            Assert.False(request.Headers.Contains("X-Hop-Request"));
            Assert.False(request.Content!.Headers.Contains("Content-Language"));
            Assert.Equal("application/json", request.Content.Headers.ContentType!.MediaType);
            Assert.Equal("kept", Assert.Single(request.Headers.GetValues("X-End-To-End")));
            Assert.Equal("http", Assert.Single(request.Headers.GetValues("X-Forwarded-Proto")));
            Assert.False(request.Headers.TryGetValues("X-Forwarded-For", out var forwarded) && forwarded.Contains("203.0.113.99"));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("response-body") };
            response.Headers.TryAddWithoutValidation("Connection", [" X-Hop-Response , content-LANGUAGE ", "keep-alive"]);
            response.Headers.TryAddWithoutValidation("X-Hop-Response", "remove");
            response.Headers.TryAddWithoutValidation("X-End-To-End", "kept");
            response.Content.Headers.ContentLanguage.Add("pt-BR");
            return Task.FromResult(response);
        });
        await using var externalGateway = CreateGatewayFactory<ExternalGatewayEntryPoint>(stub);
        await using var adminGateway = CreateGatewayFactory<AdminGatewayEntryPoint>(stub);
        using var client = admin ? adminGateway.CreateClient() : externalGateway.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("Connection", [" x-HOP-request, Content-Language ", "X-Forwarded-For, X-Forwarded-Proto"]);
        request.Headers.TryAddWithoutValidation("X-Hop-Request", "remove");
        request.Headers.TryAddWithoutValidation("X-End-To-End", "kept");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.99");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "forged");
        request.Content.Headers.ContentLanguage.Add("pt-BR");

        using var response = await client.SendAsync(request, Cancellation);

        Assert.Equal(1, downstreamCalls);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Connection"));
        Assert.False(response.Headers.Contains("X-Hop-Response"));
        Assert.Empty(response.Content.Headers.ContentLanguage);
        Assert.Equal("kept", Assert.Single(response.Headers.GetValues("X-End-To-End")));
        Assert.Equal("text/plain", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("response-body", await response.Content.ReadAsStringAsync(Cancellation));
    }

    private static WebApplicationFactory<TEntryPoint> CreateGatewayFactory<TEntryPoint>(
        IGatewayDownstreamClientFactory downstreamFactory,
        Dictionary<string, string?>? extraConfig = null)
        where TEntryPoint : class =>
        new WebApplicationFactory<TEntryPoint>()
            .WithWebHostBuilder(builder =>
            {
                if (extraConfig is not null)
                {
                    builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(extraConfig));
                }

                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IGatewayDownstreamClientFactory>();
                    services.AddSingleton(downstreamFactory);
                });
            });

    private sealed class StubDownstreamClientFactory : IGatewayDownstreamClientFactory, IDisposable
    {
        private readonly HttpMessageInvoker _invoker;

        public StubDownstreamClientFactory(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
            : this((request, _) => handler(request))
        {
        }

        public StubDownstreamClientFactory(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _invoker = new HttpMessageInvoker(new DelegatingStubHandler(handler), disposeHandler: true);
        }

        public HttpMessageInvoker CreateInvoker() => _invoker;

        public void Dispose() => _invoker.Dispose();

        private sealed class DelegatingStubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                handler(request, cancellationToken);
        }
    }
}
