using System.Net;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Ouroboros.ArchitectureTests.Controllers.Admin;
using Ouroboros.ArchitectureTests.Misplaced;

namespace Ouroboros.ArchitectureTests;

// Cada host registra só os controllers do seu público: o backoffice, os que herdam uma
// AdminControllerBase (Controllers.Admin); a API externa, todos os demais.
public sealed class HostSeparationTests(OuroborosApiFactory external, BackofficeApiFactory backoffice)
    : IClassFixture<OuroborosApiFactory>, IClassFixture<BackofficeApiFactory>
{
    [Fact]
    public async Task External_host_serves_customer_routes_but_not_backoffice_routes()
    {
        // Com token: sem ele, a política padrão responde 401 antes de saber se a rota existe.
        using var client = ProbeHost.CreateClient(external, new ProbeCallCounter());
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.Create(TestTokens.ExternalAudience, "Customer"));

        var customer = await client.GetAsync("/api/v1/probe", TestContext.Current.CancellationToken);
        var admin = await client.GetAsync("/api/v1/admin/probe", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, customer.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, admin.StatusCode);
    }

    [Fact]
    public async Task Backoffice_host_serves_backoffice_routes_but_not_customer_routes()
    {
        using var client = ProbeHost.CreateClient(backoffice, new ProbeCallCounter());
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.Create(TestTokens.BackofficeAudience, "Admin"));

        var admin = await client.GetAsync("/api/v1/admin/probe", TestContext.Current.CancellationToken);
        var customer = await client.GetAsync("/api/v1/probe", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
        Assert.Equal(AdminProbeController.Text, await admin.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.NotFound, customer.StatusCode);
    }

    // Oráculo explícito: entre os controllers do assembly de testes, só os de Controllers.Admin são de
    // backoffice. O MisplacedAdminController herda uma base com o mesmo nome fora de Controllers.Admin.
    [Fact]
    public void Each_host_registers_only_controllers_of_its_audience()
    {
        var externalControllers = TestControllers(ControllersOf(external));
        var backofficeControllers = TestControllers(ControllersOf(backoffice));

        Assert.Equal(
            [typeof(AdminProbeController), typeof(SecuredAdminProbeController)],
            backofficeControllers.OrderBy(type => type.Name));
        Assert.DoesNotContain(typeof(AdminProbeController), externalControllers);
        Assert.Contains(typeof(ProbeController), externalControllers);
        Assert.Contains(typeof(MisplacedAdminController), externalControllers);
    }

    private static List<Type> TestControllers(IEnumerable<Type> controllers) =>
        [.. controllers.Where(type => type.Assembly == typeof(HostSeparationTests).Assembly)];

    private static List<Type> ControllersOf<TEntryPoint>(WebApplicationFactory<TEntryPoint> factory)
        where TEntryPoint : class
    {
        using var client = ProbeHost.CreateClient(factory, new ProbeCallCounter(), out var host);
        return [.. host.Services
            .GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()
            .Select(action => action.ControllerTypeInfo.AsType())
            .Distinct()];
    }
}
