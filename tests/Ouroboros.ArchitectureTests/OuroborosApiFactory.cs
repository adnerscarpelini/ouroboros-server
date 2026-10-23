extern alias backoffice;
using backoffice::Ouroboros.Backoffice.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Ouroboros.ArchitectureTests;

// Sobe um host em ambiente de testes. A connection string é fictícia (estes testes não acessam o
// banco) e entra como última fonte de configuração, acima de variáveis de ambiente e user-secrets,
// para que o resultado não dependa da máquina.
public abstract class TestHostFactory<TEntryPoint>(string connectionString, IReadOnlyDictionary<string, string?>? overrides = null)
    : WebApplicationFactory<TEntryPoint>
    where TEntryPoint : class
{
    public const string FakeConnectionString = "Host=localhost;Database=ouroboros_tests;Username=tests;Password=tests";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Ouroboros"] = connectionString,
            ["Authentication:Jwt:SigningKey"] = TestTokens.SigningKey,
        };
        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
        {
            settings[key] = value;
        }

        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(settings));
    }
}

// API externa.
public sealed class OuroborosApiFactory : TestHostFactory<Program>
{
    public OuroborosApiFactory()
        : this(FakeConnectionString)
    {
    }

    internal OuroborosApiFactory(string connectionString, IReadOnlyDictionary<string, string?>? overrides = null)
        : base(connectionString, overrides)
    {
    }
}

// API de backoffice.
public sealed class BackofficeApiFactory() : TestHostFactory<BackofficeApiEntryPoint>(FakeConnectionString);
