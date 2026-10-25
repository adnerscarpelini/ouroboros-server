extern alias backoffice;

using backoffice::Ouroboros.Backoffice.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Ouroboros.IntegrationTests;

// Host agregador do backoffice, mantido até a retirada da #96.
public sealed class LegacyBackofficeHostFactory(string connectionString) : WebApplicationFactory<BackofficeApiEntryPoint>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) => TestConfiguration.Apply(builder, connectionString);
}
