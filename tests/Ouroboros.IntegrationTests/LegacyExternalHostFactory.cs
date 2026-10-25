using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Ouroboros.IntegrationTests;

// Host agregador da API externa, mantido até a retirada da #96. Serve de semente das migrations dos módulos
// (inclusive o Settings legado) e de fumaça do comportamento que o host ainda expõe.
public sealed class LegacyExternalHostFactory(string connectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) => TestConfiguration.Apply(builder, connectionString);
}
