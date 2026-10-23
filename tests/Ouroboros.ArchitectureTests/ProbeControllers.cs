using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ouroboros.ArchitectureTests;

// Controllers de teste (públicos, como os dos hosts). Os testes registram o assembly de testes como
// application part para exercitar o pipeline dos hosts: versionamento, erros e autorização.
internal static class ProbeHost
{
    public static HttpClient CreateClient<TEntryPoint>(
        WebApplicationFactory<TEntryPoint> factory,
        ProbeCallCounter counter,
        CapturingLoggerProvider? logs = null)
        where TEntryPoint : class => CreateClient(factory, counter, out _, logs);

    // Também devolve o host configurado, para inspecionar seus serviços.
    public static HttpClient CreateClient<TEntryPoint>(
        WebApplicationFactory<TEntryPoint> factory,
        ProbeCallCounter counter,
        out WebApplicationFactory<TEntryPoint> host,
        CapturingLoggerProvider? logs = null)
        where TEntryPoint : class
    {
        host = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddControllers().AddApplicationPart(typeof(ProbeHost).Assembly);
                services.AddSingleton<ProbeGreeting>();
                services.AddSingleton(counter);
                services.AddAuthorization(options => options.AddPolicy(
                    SecuredProbeController.AdminOnlyPolicy,
                    policy => policy.RequireRole("Admin")));
            });
            if (logs is not null)
            {
                builder.ConfigureLogging(logging => logging.AddProvider(logs));
            }
        });
        return host.CreateClient();
    }
}

public sealed class ProbeGreeting
{
    public const string Text = "probe controller reached";

    public string Message => Text;
}

public sealed class ProbeCallCounter
{
    private int _calls;

    public int Calls => _calls;

    public void Increment() => Interlocked.Increment(ref _calls);
}

[ApiController]
[AllowAnonymous]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/probe")]
public sealed class ProbeController(ProbeGreeting greeting) : ControllerBase
{
    [HttpGet]
    public string Get() => greeting.Message;
}

[ApiController]
[AllowAnonymous]
[ApiVersion(1, Deprecated = true)]
[ApiVersion(2)]
[Route("api/v{version:apiVersion}/versioned-probe")]
public sealed class VersionedProbeController(ProbeCallCounter counter) : ControllerBase
{
    [HttpGet]
    public string Get()
    {
        counter.Increment();
        return "versioned probe reached";
    }
}

[ApiController]
[AllowAnonymous]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/problem-probe")]
public sealed class ProblemProbeController : ControllerBase
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

public sealed record ProbeRequest([Required, EmailAddress] string Email);

// Sem [AllowAnonymous]: depende da política padrão do host, que nega quem não está autenticado.
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/secured-probe")]
public sealed class SecuredProbeController : ControllerBase
{
    public const string AdminOnlyPolicy = "probe:admin-only";

    [HttpGet]
    public string Get() => "authenticated";

    [HttpGet("admin-only")]
    [Authorize(Policy = AdminOnlyPolicy)]
    public string GetAdminOnly() => "admin only";
}
