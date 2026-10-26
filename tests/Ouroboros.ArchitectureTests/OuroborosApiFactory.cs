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

namespace Ouroboros.ArchitectureTests;

// Sobe a API de Identity em ambiente de testes. A connection string é fictícia (estes testes não acessam o
// banco) e entra como última fonte de configuração, acima de variáveis de ambiente e user-secrets,
// para que o resultado não dependa da máquina.
//
// O TestServer não tem sockets, então o listener vem do cabeçalho de teste X-Test-Listener, lido só pelo
// resolvedor registrado aqui (o código de produção nunca lê cabeçalho para decidir o público). A separação
// por portas do Kestrel é verificada nos testes de integração.
public class OuroborosApiFactory : WebApplicationFactory<IdentityApiEntryPoint>
{
    public const string FakeConnectionString = "Host=localhost;Database=ouroboros_tests;Username=tests;Password=tests";

    public const string ListenerHeader = "X-Test-Listener";

    public const string AdminListener = "admin";

    private readonly string _connectionString;

    private readonly IReadOnlyDictionary<string, string?>? _overrides;

    public OuroborosApiFactory()
        : this(FakeConnectionString)
    {
    }

    internal OuroborosApiFactory(string connectionString, IReadOnlyDictionary<string, string?>? overrides = null)
    {
        _connectionString = connectionString;
        _overrides = overrides;
    }

    protected virtual bool DefaultsToAdminListener => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Ouroboros"] = _connectionString,
            ["Authentication:Jwt:KeyRingPath"] = TestSigningKeys.ManifestPath,
            ["Identity:EmailIntents:PublicKeyRingPath"] = TestSigningKeys.EmailPublicKeyRingPath,
            ["Identity:EmailIntents:SupersessionKeyPath"] = TestSigningKeys.SupersessionKeyPath,
            ["Identity:EmailIntents:MaintenanceEnabled"] = "false",
        };
        foreach (var (key, value) in _overrides ?? new Dictionary<string, string?>())
        {
            settings[key] = value;
        }

        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(settings));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IListenerRoleResolver>();
            services.AddSingleton<IListenerRoleResolver, HeaderListenerRoleResolver>();
        });
    }

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        if (DefaultsToAdminListener)
        {
            client.DefaultRequestHeaders.Add(ListenerHeader, AdminListener);
        }
    }
}

// Todas as requisições do cliente chegam ao listener administrativo.
public sealed class BackofficeApiFactory : OuroborosApiFactory
{
    protected override bool DefaultsToAdminListener => true;
}

internal sealed class HeaderListenerRoleResolver : IListenerRoleResolver
{
    public ListenerRole Resolve(HttpContext context) =>
        context.Request.Headers[OuroborosApiFactory.ListenerHeader] == OuroborosApiFactory.AdminListener
            ? ListenerRole.Admin
            : ListenerRole.Public;
}
