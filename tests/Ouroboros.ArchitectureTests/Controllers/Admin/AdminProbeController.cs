using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;

namespace Ouroboros.ArchitectureTests.Controllers.Admin;

// Reproduz, no assembly de testes, a estrutura de backoffice de um módulo (Controllers.Admin com uma
// AdminControllerBase), para verificar em qual host cada controller é registrado.
[ApiController]
internal abstract class AdminControllerBase : ControllerBase;

[ApiVersion(1)]
[Route("api/v{version:apiVersion}/admin/probe")]
internal sealed class AdminProbeController : AdminControllerBase
{
    public const string Text = "admin probe reached";

    [HttpGet]
    public string Get() => Text;
}
