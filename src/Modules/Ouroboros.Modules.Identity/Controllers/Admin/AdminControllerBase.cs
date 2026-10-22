using Microsoft.AspNetCore.Mvc;

namespace Ouroboros.Modules.Identity.Controllers.Admin;

// Base obrigatória dos controllers de backoffice (rotas api/v{version:apiVersion}/admin/identity/...),
// verificada por teste de arquitetura. A exigência do papel Admin entra aqui com a autenticação JWT (#9).
[ApiController]
internal abstract class AdminControllerBase : ControllerBase;
