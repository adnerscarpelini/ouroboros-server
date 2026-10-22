using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Ouroboros.ArchitectureTests;

// Os módulos ainda não têm controllers concretos. Este teste registra uma application part com um
// controller internal, como um módulo faria, e prova que o host o descobre e o ativa com uma
// dependência internal.
public sealed class ControllerDiscoveryTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Internal_controller_in_application_part_responds_on_its_route()
    {
        using var client = factory
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.AddControllers().AddApplicationPart(typeof(ProbeController).Assembly);
                services.AddSingleton<ProbeGreeting>();
            }))
            .CreateClient();

        var response = await client.GetAsync("/api/probe", TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.Equal(ProbeGreeting.Text, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}

internal sealed class ProbeGreeting
{
    public const string Text = "internal controller reached";

    public string Message => Text;
}

[ApiController]
[Route("api/probe")]
internal sealed class ProbeController(ProbeGreeting greeting) : ControllerBase
{
    [HttpGet]
    public string Get() => greeting.Message;
}
