extern alias backoffice;

using backoffice::Ouroboros.Backoffice.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Ouroboros.IntegrationTests;

// Sobe o host de backoffice apontando para o mesmo banco do teste, com a configuração da IntegrationApiFactory.
public sealed class BackofficeIntegrationApiFactory(string connectionString) : WebApplicationFactory<BackofficeApiEntryPoint>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) => IntegrationApiFactory.Configure(builder, connectionString);
}
