using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ouroboros.ArchitectureTests.Controllers.Admin;

// Reproduz, no assembly de testes, a estrutura de backoffice de um módulo (Controllers.Admin com uma
// AdminControllerBase), para verificar em qual host cada controller é registrado.
[ApiController]
internal abstract class AdminControllerBase : ControllerBase;

// Anônimo para testar só o roteamento entre hosts; a autorização é testada em SecuredAdminProbeController.
[AllowAnonymous]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/admin/probe")]
internal sealed class AdminProbeController : AdminControllerBase
{
    public const string Text = "admin probe reached";

    [HttpGet]
    public string Get() => Text;
}

// Sem política própria nem [Authorize] na base de teste: quem protege é a política global do backoffice.
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/admin/secured-probe")]
internal sealed class SecuredAdminProbeController : AdminControllerBase
{
    [HttpGet]
    public string Get() => "admin";

    [HttpGet("with-policy")]
    [Authorize(Policy = SecuredProbeController.AdminOnlyPolicy)]
    public string GetWithPolicy() => "admin with policy";
}
