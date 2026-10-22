extern alias backoffice;
using backoffice::Ouroboros.Backoffice.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Ouroboros.ArchitectureTests;

// Sobe um host em ambiente de testes. A connection string é fictícia (estes testes não acessam o
// banco) e entra como última fonte de configuração, acima de variáveis de ambiente e user-secrets,
// para que o resultado não dependa da máquina.
public abstract class TestHostFactory<TEntryPoint>(string connectionString) : WebApplicationFactory<TEntryPoint>
    where TEntryPoint : class
{
    public const string FakeConnectionString = "Host=localhost;Database=ouroboros_tests;Username=tests;Password=tests";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:Ouroboros"] = connectionString }));
    }
}

// API externa.
public sealed class OuroborosApiFactory : TestHostFactory<Program>
{
    public OuroborosApiFactory()
        : this(FakeConnectionString)
    {
    }

    internal OuroborosApiFactory(string connectionString)
        : base(connectionString)
    {
    }
}

// API de backoffice.
public sealed class BackofficeApiFactory() : TestHostFactory<BackofficeApiEntryPoint>(FakeConnectionString);
