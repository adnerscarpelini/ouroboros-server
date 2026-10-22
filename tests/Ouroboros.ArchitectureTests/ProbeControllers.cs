using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ouroboros.ArchitectureTests;

// Os módulos ainda não têm controllers concretos. Os testes registram o assembly de testes como
// application part, como um módulo faria, para exercitar a descoberta e o versionamento.
internal static class ProbeHost
{
    public static HttpClient CreateClient(
        OuroborosApiFactory factory,
        ProbeCallCounter counter,
        CapturingLoggerProvider? logs = null) =>
        factory
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.AddControllers().AddApplicationPart(typeof(ProbeHost).Assembly);
                    services.AddSingleton<ProbeGreeting>();
                    services.AddSingleton(counter);
                });
                if (logs is not null)
                {
                    builder.ConfigureLogging(logging => logging.AddProvider(logs));
                }
            })
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

[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/problem-probe")]
internal sealed class ProblemProbeController : ControllerBase
{
    public const string InternalDetail = "detalhe interno que nunca deve sair na resposta";
    public const string BusinessProblemType = "https://github.com/adnerscarpelini/ouroboros-server/blob/main/docs/errors/probe.md#probe-conflict";

    [HttpGet("unexpected")]
    public IActionResult Unexpected() => throw new InvalidOperationException(InternalDetail);

    [HttpPost("validation")]
    public IActionResult Validation(ProbeRequest request) => Ok(request.Email);

    [HttpGet("business")]
    public IActionResult Business() =>
        Problem(type: BusinessProblemType, title: "Conflito de teste.", statusCode: StatusCodes.Status409Conflict);
}

internal sealed record ProbeRequest([Required, EmailAddress] string Email);
