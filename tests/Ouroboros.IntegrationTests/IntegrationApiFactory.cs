extern alias identityapi;

using identityapi::Ouroboros.Identity.Api;
using identityapi::Ouroboros.Identity.Api.Listeners;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ouroboros.IntegrationTests;

// Sobe a API de Identity apontando para um banco do container. A connection string entra como última fonte
// de configuração, acima de variáveis de ambiente e user-secrets, para não depender da máquina.
//
// O TestServer não tem sockets, então o listener de cada requisição vem do cabeçalho de teste
// X-Test-Listener (admin ou ausente), lido só por HeaderListenerRoleResolver, que existe neste projeto de teste.
// A separação real por portas do Kestrel é verificada em KestrelListenerTests.
public class IntegrationApiFactory(string connectionString) : WebApplicationFactory<IdentityApiEntryPoint>
{
    // Chave fictícia de 256+ bits, só para testes.
    public const string SigningKey = "integration-tests-signing-key-with-at-least-256-bits";

    public const string ListenerHeader = "X-Test-Listener";

    public const string AdminListener = "admin";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        TestConfiguration.Apply(builder, connectionString);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IListenerRoleResolver>();
            services.AddSingleton<IListenerRoleResolver, HeaderListenerRoleResolver>();
        });
    }

    // Cliente que fala com o listener administrativo.
    public HttpClient CreateAdminClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(ListenerHeader, AdminListener);
        return client;
    }
}

// Todas as requisições do cliente chegam ao listener administrativo.
public sealed class BackofficeIntegrationApiFactory(string connectionString) : IntegrationApiFactory(connectionString)
{
    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        client.DefaultRequestHeaders.Add(ListenerHeader, AdminListener);
    }
}

internal sealed class HeaderListenerRoleResolver : IListenerRoleResolver
{
    public ListenerRole Resolve(HttpContext context) =>
        context.Request.Headers[IntegrationApiFactory.ListenerHeader] == IntegrationApiFactory.AdminListener
            ? ListenerRole.Admin
            : ListenerRole.Public;
}

internal static class TestConfiguration
{
    // Configuração comum aos testes da API de Identity.
    internal static void Apply(IWebHostBuilder builder, string connectionString)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:Ouroboros"] = connectionString,
                ["Authentication:Jwt:KeyRingPath"] = TestSigningKeys.ManifestPath,
                ["Identity:EmailIntents:PublicKeyRingPath"] = TestSigningKeys.EmailPublicKeyRingPath,
                ["Identity:EmailIntents:SupersessionKeyPath"] = TestSigningKeys.SupersessionKeyPath,
                ["Identity:EmailIntents:MaintenanceEnabled"] = "false",

                // No TestServer todas as requisições vêm do mesmo IP (desconhecido); o limite do login fica
                // alto para não interferir nos outros testes. RateLimitingTests usa limites próprios.
                ["Identity:RateLimiting:Policies:identity-login:PermitLimit"] = "100000",
                ["Identity:RateLimiting:Policies:identity-refresh:PermitLimit"] = "100000",
                ["Identity:RateLimiting:Policies:identity-logout:PermitLimit"] = "100000",
                ["Identity:RateLimiting:Policies:identity-registration:PermitLimit"] = "100000",
                ["Identity:RateLimiting:Policies:identity-registration-completion:PermitLimit"] = "100000",
                ["Identity:RateLimiting:Policies:identity-password-reset:PermitLimit"] = "100000",
                ["Identity:RateLimiting:Policies:identity-password-reset-completion:PermitLimit"] = "100000",
            }));

        // Controllers de teste (por exemplo, a sonda de migrations) ficam neste assembly.
        builder.ConfigureServices(services =>
            services.AddControllers().AddApplicationPart(typeof(TestConfiguration).Assembly));
    }
}
