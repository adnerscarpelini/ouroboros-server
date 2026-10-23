using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ouroboros.IntegrationTests;

// Sobe a API apontando para um banco do container. A connection string entra como última fonte de
// configuração, acima de variáveis de ambiente e user-secrets, para não depender da máquina.
public sealed class IntegrationApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    // Chave fictícia de 256+ bits, só para testes.
    public const string SigningKey = "integration-tests-signing-key-with-at-least-256-bits";

    protected override void ConfigureWebHost(IWebHostBuilder builder) => Configure(builder, connectionString);

    // Mesma configuração para os dois hosts (veja BackofficeIntegrationApiFactory).
    internal static void Configure(IWebHostBuilder builder, string connectionString)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:Ouroboros"] = connectionString,
                ["Authentication:Jwt:SigningKey"] = SigningKey,
            }));

        // Controllers de teste (por exemplo, a sonda de migrations) ficam neste assembly.
        builder.ConfigureServices(services =>
            services.AddControllers().AddApplicationPart(typeof(IntegrationApiFactory).Assembly));
    }
}
