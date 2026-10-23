using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Ouroboros.Modules.Identity.Domain.Users;

namespace Ouroboros.Modules.Identity.Controllers.Admin;

// Base obrigatória dos controllers de backoffice (rotas api/v{version:apiVersion}/admin/identity/...),
// verificada por teste de arquitetura. Exige o papel Admin em todo o backoffice do módulo, mesmo
// que uma action esqueça de declarar sua política.
[ApiController]
[Authorize(Roles = nameof(UserRole.Admin))]
internal abstract class AdminControllerBase : ControllerBase;
