using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Ouroboros.ArchitectureTests;

// Sobe o host em ambiente de testes. A connection string é fictícia (estes testes não acessam o
// banco) e entra como última fonte de configuração, acima de variáveis de ambiente e user-secrets,
// para que o resultado não dependa da máquina.
public sealed class OuroborosApiFactory : WebApplicationFactory<Program>
{
    public const string FakeConnectionString = "Host=localhost;Database=ouroboros_tests;Username=tests;Password=tests";

    private readonly string _connectionString;

    public OuroborosApiFactory()
        : this(FakeConnectionString)
    {
    }

    internal OuroborosApiFactory(string connectionString) => _connectionString = connectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:Ouroboros"] = _connectionString }));
    }
}
