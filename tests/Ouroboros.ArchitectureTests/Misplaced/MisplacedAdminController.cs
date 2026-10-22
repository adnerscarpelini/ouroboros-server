using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;

namespace Ouroboros.ArchitectureTests.Misplaced;

// Uma base com o mesmo nome, mas fora de Controllers.Admin, não marca o controller como de
// backoffice: ele deve ficar no host externo.
[ApiController]
internal abstract class AdminControllerBase : ControllerBase;

[ApiVersion(1)]
[Route("api/v{version:apiVersion}/misplaced-probe")]
internal sealed class MisplacedAdminController : AdminControllerBase
{
    [HttpGet]
    public string Get() => "misplaced";
}
