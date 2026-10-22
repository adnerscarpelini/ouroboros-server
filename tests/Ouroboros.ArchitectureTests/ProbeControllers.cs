using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Ouroboros.ArchitectureTests;

// Os módulos ainda não têm controllers concretos. Os testes registram o assembly de testes como
// application part, como um módulo faria, para exercitar a descoberta e o versionamento.
internal static class ProbeHost
{
    public static HttpClient CreateClient(OuroborosApiFactory factory, ProbeCallCounter counter) =>
        factory
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.AddControllers().AddApplicationPart(typeof(ProbeHost).Assembly);
                services.AddSingleton<ProbeGreeting>();
                services.AddSingleton(counter);
            }))
            .CreateClient();
}

internal sealed class ProbeGreeting
{
    public const string Text = "internal controller reached";

    public string Message => Text;
}

internal sealed class ProbeCallCounter
{
    private int _calls;

    public int Calls => _calls;

    public void Increment() => Interlocked.Increment(ref _calls);
}

[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/probe")]
internal sealed class ProbeController(ProbeGreeting greeting) : ControllerBase
{
    [HttpGet]
    public string Get() => greeting.Message;
}

[ApiController]
[ApiVersion(1, Deprecated = true)]
[ApiVersion(2)]
[Route("api/v{version:apiVersion}/versioned-probe")]
internal sealed class VersionedProbeController(ProbeCallCounter counter) : ControllerBase
{
    [HttpGet]
    public string Get()
    {
        counter.Increment();
        return "versioned probe reached";
    }
}
