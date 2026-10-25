using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Ouroboros.Identity.Contracts;
using Ouroboros.Identity.Contracts.Sessions;
using Ouroboros.Identity.Contracts.Users;

namespace Ouroboros.Api.Controllers.Identity;

// Dados do cliente autenticado, lidos do banco a cada chamada.
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/identity/me")]
public sealed class MeController(IGetCurrentUser getCurrentUser) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<MeResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        // Usuário inexistente, inativo ou de outro papel recebe 401, mesmo com um token ainda válido.
        var user = Guid.TryParse(User.FindFirst(IdentityClaims.Subject)?.Value, out var userId)
            ? await getCurrentUser.ExecuteAsync(new GetCurrentUserRequest(userId, LoginChannel.CustomerApp), cancellationToken)
            : null;

        if (user is null)
        {
            // Como no 401 da autenticação (RFC 9110, seção 11.6.1).
            Response.Headers[HeaderNames.WWWAuthenticate] = "Bearer";
            return Problem(statusCode: StatusCodes.Status401Unauthorized);
        }

        return Ok(new MeResponse(user.Id, user.Email, user.Role));
    }
}

// Lista fechada de campos da resposta; um teste falha se ela mudar sem atualizar a lista permitida.
public sealed record MeResponse(Guid Id, string Email, string Role);
