using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ouroboros.ArchitectureTests.Controllers.Admin;

// Controller de backoffice de teste sem nenhuma política própria: quem o protege é a política global
// do host de backoffice, que exige o papel Admin.
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/admin/secured-probe")]
public sealed class SecuredAdminProbeController : ControllerBase
{
    [HttpGet]
    public string Get() => "admin";

    [HttpGet("with-policy")]
    [Authorize(Policy = SecuredProbeController.AdminOnlyPolicy)]
    public string GetWithPolicy() => "admin with policy";
}
