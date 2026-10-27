extern alias admingateway;
extern alias externalgateway;
extern alias identityapi;

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using admingateway::Ouroboros.AdminGateway.Api;
using externalgateway::Ouroboros.ExternalGateway.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Ouroboros.Hosting;
using Ouroboros.Hosting.Gateway;
using Ouroboros.Identity.Contracts.Administrators;
using Ouroboros.Identity.Infrastructure.Persistence;

namespace Ouroboros.IntegrationTests;

public sealed class GatewayIntegrationTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private const string PeerIpHeader = "X-Test-Peer-IP";
    private const string GatewayClientIpHeader = "X-Test-Gateway-Client-IP";
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task External_and_admin_gateways_forward_compatible_routes_and_preserve_service_authorization()
    {
        const string adminEmail = "gateway-admin@adnerscarpelini.com";
        const string adminPassword = "uma frase longa e segura para o admin do gateway";

        await using (var scope = Api.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            await db.Sessions.ExecuteDeleteAsync(Cancellation);
            await db.Users.ExecuteDeleteAsync(Cancellation);
            var createAdmin = scope.ServiceProvider.GetRequiredService<ICreateFirstAdmin>();
            Assert.True((await createAdmin.ExecuteAsync(new CreateFirstAdminRequest(adminEmail, adminPassword), Cancellation)).Succeeded);
        }

        await using var externalGateway = CreateExternalGateway(Api, gatewayPeerIp: "10.240.0.10");
        await using var adminGateway = CreateAdminGateway(Api, gatewayPeerIp: "127.0.0.1");
        using var externalGatewayClient = externalGateway.CreateClient();
        using var adminGatewayClient = adminGateway.CreateClient();
        using var directPublicClient = Api.CreateClient();
        using var directAdminClient = Api.CreateAdminClient();

        // 1. Contrato e rotas públicas compatíveis via gateway externo e diretamente no serviço.
        using (var directKeys = await directPublicClient.GetAsync("/api/v1/identity/keys", Cancellation))
        using (var gatewayKeys = await externalGatewayClient.GetAsync("/api/v1/identity/keys", Cancellation))
        {
            Assert.Equal(HttpStatusCode.OK, directKeys.StatusCode);
            Assert.Equal(HttpStatusCode.OK, gatewayKeys.StatusCode);
            Assert.Equal(
                await directKeys.Content.ReadAsStringAsync(Cancellation),
                await gatewayKeys.Content.ReadAsStringAsync(Cancellation));
        }

        using (var registration = await externalGatewayClient.PostAsJsonAsync(
            "/api/v1/identity/registrations",
            new { Email = "via-external-gateway@example.com" },
            Cancellation))
        {
            Assert.Equal(HttpStatusCode.Accepted, registration.StatusCode);
        }

        // 2. Rota administrativa chamada no gateway externo retorna 404 (bloqueada na borda);
        //    chamada diretamente no listener público de Identity responde como rota inexistente (401 anônimo pela FallbackPolicy / 404 autenticado).
        using (var adminOnExternalGateway = await externalGatewayClient.GetAsync("/api/v1/admin/identity/me", Cancellation))
        using (var adminOnDirectPublicAnon = await directPublicClient.GetAsync("/api/v1/admin/identity/me", Cancellation))
        using (var nonexistentOnDirectPublicAnon = await directPublicClient.GetAsync("/api/v1/admin/identity/nao-existe", Cancellation))
        {
            Assert.Equal(HttpStatusCode.NotFound, adminOnExternalGateway.StatusCode);
            Assert.Equal(nonexistentOnDirectPublicAnon.StatusCode, adminOnDirectPublicAnon.StatusCode);
        }

        // 3. Rota externa chamada no gateway administrativo retorna 404 (bloqueada na borda);
        //    chamada diretamente no listener administrativo de Identity responde como rota inexistente.
        using (var externalOnAdminGateway = await adminGatewayClient.GetAsync("/api/v1/identity/keys", Cancellation))
        using (var externalOnDirectAdminAnon = await directAdminClient.GetAsync("/api/v1/identity/keys", Cancellation))
        using (var nonexistentOnDirectAdminAnon = await directAdminClient.GetAsync("/api/v1/identity/nao-existe", Cancellation))
        {
            Assert.Equal(HttpStatusCode.NotFound, externalOnAdminGateway.StatusCode);
            Assert.Equal(nonexistentOnDirectAdminAnon.StatusCode, externalOnDirectAdminAnon.StatusCode);
        }

        // 4. Autorização preservada no proprietário (Identity) tanto via gateway quanto diretamente:
        //    - Token de audiência externa (ouroboros-api) é recusado com 401 na rota administrativa.
        //    - Token de audiência administrativa (ouroboros-backoffice) com papel Customer é recusado com 403.
        var customerAudienceToken = CreateToken("ouroboros-api", "Admin");
        var wrongRoleAdminAudienceToken = CreateToken("ouroboros-backoffice", "Customer");

        foreach (var client in new[] { adminGatewayClient, directAdminClient })
        {
            using var unauthenticated = await client.GetAsync("/api/v1/admin/identity/me", Cancellation);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

            using var wrongAudienceReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/identity/me");
            wrongAudienceReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", customerAudienceToken);
            using var wrongAudienceRes = await client.SendAsync(wrongAudienceReq, Cancellation);
            Assert.Equal(HttpStatusCode.Unauthorized, wrongAudienceRes.StatusCode);

            using var wrongRoleReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/identity/me");
            wrongRoleReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", wrongRoleAdminAudienceToken);
            using var wrongRoleRes = await client.SendAsync(wrongRoleReq, Cancellation);
            Assert.Equal(HttpStatusCode.Forbidden, wrongRoleRes.StatusCode);
        }

        // 5. Login administrativo real e consulta /api/v1/admin/identity/me funcionam via AdminGateway.
        using var adminLogin = await adminGatewayClient.PostAsJsonAsync(
            "/api/v1/admin/identity/sessions",
            new { Email = adminEmail, Password = adminPassword },
            Cancellation);
        Assert.Equal(HttpStatusCode.OK, adminLogin.StatusCode);
        using var loginJson = JsonDocument.Parse(await adminLogin.Content.ReadAsStringAsync(Cancellation));
        var adminAccessToken = loginJson.RootElement.GetProperty("accessToken").GetString()!;

        using var adminMeReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/identity/me");
        adminMeReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminAccessToken);
        using var adminMeRes = await adminGatewayClient.SendAsync(adminMeReq, Cancellation);
        Assert.Equal(HttpStatusCode.OK, adminMeRes.StatusCode);
        using var meJson = JsonDocument.Parse(await adminMeRes.Content.ReadAsStringAsync(Cancellation));
        Assert.Equal(adminEmail, meJson.RootElement.GetProperty("email").GetString());

        foreach (var client in new[] { adminGatewayClient, directAdminClient })
        {
            using var parameters = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/settings/parameters?limit=1");
            parameters.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminAccessToken);
            using var parametersResponse = await client.SendAsync(parameters, Cancellation);
            Assert.Equal(HttpStatusCode.OK, parametersResponse.StatusCode);
            using var historyRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/settings/parameters/Identity:Lockout:MaxFailedAttempts/history?limit=1");
            historyRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminAccessToken);
            using var historyResponse = await client.SendAsync(historyRequest, Cancellation);
            Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);
            using var wrongRole = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/settings/parameters");
            wrongRole.Headers.Authorization = new AuthenticationHeaderValue("Bearer", wrongRoleAdminAudienceToken);
            using var refused = await client.SendAsync(wrongRole, Cancellation);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }
        using var parametersOnExternal = await externalGatewayClient.GetAsync("/api/v1/admin/settings/parameters", Cancellation);
        Assert.Equal(HttpStatusCode.NotFound, parametersOnExternal.StatusCode);

        // 6. Readiness do gateway reflete a prontidão do Identity sem expor segredos.
        using var gatewayReady = await externalGatewayClient.GetAsync(OuroborosHealthExtensions.ReadyPath, Cancellation);
        Assert.Equal(HttpStatusCode.OK, gatewayReady.StatusCode);
        Assert.Equal("{\"status\":\"Healthy\"}", await gatewayReady.Content.ReadAsStringAsync(Cancellation));
    }

    [Fact]
    public async Task Rate_limiting_uses_real_client_ip_only_from_configured_gateway_proxies_and_prevents_replica_or_header_spoofing_bypass()
    {
        const string ConfiguredGatewayReplica1Ip = "10.240.0.10";
        const string ConfiguredGatewayReplica2Ip = "10.240.0.11";
        const string UntrustedDirectCallerIp = "192.0.2.55";

        await using var rateLimitedIdentity = Api.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Identity:RateLimiting:Policies:identity-login:PermitLimit"] = "2",
                ["Identity:RateLimiting:Policies:identity-login:Window"] = "01:00:00",
                ["Identity:ForwardedHeaders:KnownProxies:0"] = ConfiguredGatewayReplica1Ip,
                ["Identity:ForwardedHeaders:KnownProxies:1"] = ConfiguredGatewayReplica2Ip,
            }));
            builder.ConfigureTestServices(services =>
                services.AddTransient<IStartupFilter, PeerIpStartupFilter>());
        });

        await using var gatewayReplica1 = CreateExternalGateway(rateLimitedIdentity, ConfiguredGatewayReplica1Ip);
        await using var gatewayReplica2 = CreateExternalGateway(rateLimitedIdentity, ConfiguredGatewayReplica2Ip);
        using var replica1Client = gatewayReplica1.CreateClient();
        using var replica2Client = gatewayReplica2.CreateClient();

        // Cliente real 198.51.100.10 alterna entre duas réplicas do gateway e tenta forjar X-Forwarded-For:
        // O gateway descarta o X-Forwarded-For forjado, aplica 198.51.100.10 e o Identity compartilha o contador
        // do mesmo IP de cliente entre as réplicas de gateway.
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await SendGatewayLoginAsync(replica1Client, "198.51.100.10", forgedForwardedFor: "203.0.113.100")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await SendGatewayLoginAsync(replica2Client, "198.51.100.10", forgedForwardedFor: "203.0.113.101")).StatusCode);
        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            (await SendGatewayLoginAsync(replica1Client, "198.51.100.10", forgedForwardedFor: "203.0.113.102")).StatusCode);

        // Outro cliente real (198.51.100.20) passando pelo mesmo gateway não é afetado pelo bloqueio do primeiro.
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await SendGatewayLoginAsync(replica1Client, "198.51.100.20")).StatusCode);

        // Chamada direta (ou de proxy não configurado em Identity:ForwardedHeaders:KnownProxies) tentando
        // burlar o limite com cabeçalhos X-Forwarded-For falsificados é bloqueada pelo IP da conexão (192.0.2.55).
        using var directClient = rateLimitedIdentity.CreateClient();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await SendDirectLoginAsync(directClient, UntrustedDirectCallerIp, forgedForwardedFor: "198.51.100.201")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await SendDirectLoginAsync(directClient, UntrustedDirectCallerIp, forgedForwardedFor: "198.51.100.202")).StatusCode);
        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            (await SendDirectLoginAsync(directClient, UntrustedDirectCallerIp, forgedForwardedFor: "198.51.100.203")).StatusCode);
    }

    private static WebApplicationFactory<ExternalGatewayEntryPoint> CreateExternalGateway(
        WebApplicationFactory<identityapi::Ouroboros.Identity.Api.IdentityApiEntryPoint> downstreamIdentity,
        string gatewayPeerIp) =>
        new WebApplicationFactory<ExternalGatewayEntryPoint>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Gateway:Downstream:IdentityBaseUrl"] = "http://localhost",
                    ["Gateway:Downstream:Timeout"] = "00:00:05",
                }));
                builder.ConfigureTestServices(services =>
                {
                    services.AddTransient<IStartupFilter, GatewayClientIpStartupFilter>();
                    services.RemoveAll<IGatewayDownstreamClientFactory>();
                    services.AddSingleton<IGatewayDownstreamClientFactory>(
                        new InMemoryIdentityDownstreamFactory(downstreamIdentity, gatewayPeerIp, adminListener: false));
                });
            });

    private static WebApplicationFactory<AdminGatewayEntryPoint> CreateAdminGateway(
        WebApplicationFactory<identityapi::Ouroboros.Identity.Api.IdentityApiEntryPoint> downstreamIdentity,
        string gatewayPeerIp) =>
        new WebApplicationFactory<AdminGatewayEntryPoint>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Gateway:Downstream:IdentityBaseUrl"] = "http://127.0.0.1",
                    ["Gateway:Downstream:Timeout"] = "00:00:05",
                }));
                builder.ConfigureTestServices(services =>
                {
                    services.AddTransient<IStartupFilter, GatewayClientIpStartupFilter>();
                    services.RemoveAll<IGatewayDownstreamClientFactory>();
                    services.AddSingleton<IGatewayDownstreamClientFactory>(
                        new InMemoryIdentityDownstreamFactory(downstreamIdentity, gatewayPeerIp, adminListener: true));
                });
            });

    private static async Task<HttpResponseMessage> SendGatewayLoginAsync(
        HttpClient gatewayClient,
        string realClientIp,
        string? forgedForwardedFor = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/identity/sessions")
        {
            Content = JsonContent.Create(new { Email = "nobody@example.com", Password = "uma senha qualquer bem longa" }),
        };
        request.Headers.Add(GatewayClientIpHeader, realClientIp);
        if (forgedForwardedFor is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", forgedForwardedFor);
        }

        return await gatewayClient.SendAsync(request, Cancellation);
    }

    private static async Task<HttpResponseMessage> SendDirectLoginAsync(
        HttpClient directIdentityClient,
        string peerIp,
        string forgedForwardedFor)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/identity/sessions")
        {
            Content = JsonContent.Create(new { Email = "nobody@example.com", Password = "uma senha qualquer bem longa" }),
        };
        request.Headers.Add(PeerIpHeader, peerIp);
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", forgedForwardedFor);
        return await directIdentityClient.SendAsync(request, Cancellation);
    }

    private static string CreateToken(string audience, string role)
    {
        var now = DateTime.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = "ouroboros",
            Audience = audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(15),
            Subject = new ClaimsIdentity(
            [
                new Claim("sub", Guid.CreateVersion7().ToString()),
                new Claim("role", role),
                new Claim("principal_type", "user"),
                new Claim("sid", Guid.CreateVersion7().ToString()),
            ]),
            SigningCredentials = new SigningCredentials(TestSigningKeys.Key, SecurityAlgorithms.RsaSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private sealed class GatewayClientIpStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (IPAddress.TryParse(context.Request.Headers[GatewayClientIpHeader], out var ip))
                {
                    context.Connection.RemoteIpAddress = ip;
                    context.Request.Headers.Remove(GatewayClientIpHeader);
                }

                return nextMiddleware(context);
            });
            next(app);
        };
    }

    private sealed class PeerIpStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (IPAddress.TryParse(context.Request.Headers[PeerIpHeader], out var ip))
                {
                    context.Connection.RemoteIpAddress = ip;
                    context.Request.Headers.Remove(PeerIpHeader);
                }

                return nextMiddleware(context);
            });
            next(app);
        };
    }

    private sealed class InMemoryIdentityDownstreamFactory : IGatewayDownstreamClientFactory, IDisposable
    {
        private readonly HttpMessageInvoker _invoker;

        public InMemoryIdentityDownstreamFactory(
            WebApplicationFactory<identityapi::Ouroboros.Identity.Api.IdentityApiEntryPoint> identityFactory,
            string gatewayPeerIp,
            bool adminListener)
        {
            var innerHandler = identityFactory.Server.CreateHandler();
            _invoker = new HttpMessageInvoker(new GatewayPeerDelegatingHandler(innerHandler, gatewayPeerIp, adminListener), disposeHandler: true);
        }

        public HttpMessageInvoker CreateInvoker() => _invoker;

        public void Dispose() => _invoker.Dispose();

        private sealed class GatewayPeerDelegatingHandler(HttpMessageHandler inner, string gatewayPeerIp, bool adminListener)
            : DelegatingHandler(inner)
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                request.Headers.Remove(PeerIpHeader);
                request.Headers.TryAddWithoutValidation(PeerIpHeader, gatewayPeerIp);
                if (adminListener)
                {
                    request.Headers.Remove(IntegrationApiFactory.ListenerHeader);
                    request.Headers.TryAddWithoutValidation(IntegrationApiFactory.ListenerHeader, IntegrationApiFactory.AdminListener);
                }

                return base.SendAsync(request, cancellationToken);
            }
        }
    }
}
