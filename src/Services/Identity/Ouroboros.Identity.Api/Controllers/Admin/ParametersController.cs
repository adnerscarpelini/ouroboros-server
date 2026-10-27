using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Ouroboros.Identity.Contracts;
using Ouroboros.Identity.Contracts.Parameters;
using Ouroboros.Identity.Contracts.Sessions;
using Ouroboros.Identity.Contracts.Users;

namespace Ouroboros.Identity.Api.Controllers.Admin;

// Rota v1 compatível; os dados e a autorização pertencem a Identity, apesar do segmento histórico settings.
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/admin/settings/parameters")]
public sealed class ParametersController(IListParameters listParameters, IGetCurrentUser currentUser, IGetParameterHistory history) : ControllerBase
{
    [HttpGet("{key}/history")]
    [Authorize(Policy = IdentityPolicies.ParametersRead)]
    public async Task<IActionResult> GetHistory(string key, [FromQuery] int limit = 50, [FromQuery] string? after = null, CancellationToken cancellationToken = default)
    {
        if (!await IsActiveAdminAsync(cancellationToken)) { return Unauthorized(); }
        if (Request.Query.ContainsKey("after") && after is null) { return Problem(statusCode: 400, title: "Cursor inválido."); }
        try { return Ok(await history.ExecuteAsync(new(key, new(limit, after)), cancellationToken)); }
        catch (InvalidPageException) { return Problem(statusCode: 400, title: "Limite ou cursor inválido para esta consulta."); }
        catch (ParameterNotFoundException) { return Problem(statusCode: 404, title: "Parâmetro não encontrado."); }
    }

    [HttpGet]
    [Authorize(Policy = IdentityPolicies.ParametersRead)]
    public async Task<IActionResult> Get([FromQuery] int limit = 50, [FromQuery] string? after = null, CancellationToken cancellationToken = default)
    {
        if (!await IsActiveAdminAsync(cancellationToken)) { return Unauthorized(); }
        if (Request.Query.ContainsKey("after") && after is null) { return Problem(statusCode: 400, title: "Cursor inválido."); }
        try { return Ok(await listParameters.ExecuteAsync(new(limit, after), cancellationToken)); }
        catch (InvalidPageException) { return Problem(statusCode: 400, title: "Limite ou cursor inválido para esta consulta."); }
    }

    private async Task<bool> IsActiveAdminAsync(CancellationToken cancellationToken) =>
        Guid.TryParse(User.FindFirst(IdentityClaims.Subject)?.Value, out var userId)
        && await currentUser.ExecuteAsync(new(userId, LoginChannel.Backoffice), cancellationToken) is not null;
}
