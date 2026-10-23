using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Ouroboros.Modules.Identity.Contracts;
using Ouroboros.Modules.Identity.Contracts.Sessions;

namespace Ouroboros.Api.Controllers.Identity;

// Login do cliente. Só aceita usuários Customer; credenciais de um Admin recebem o mesmo 401 de
// senha errada.
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/identity/sessions")]
public sealed class SessionsController(ILogin login) : ControllerBase
{
    [HttpPost]
    [AllowAnonymous]
    [EnableRateLimiting(IdentityRateLimitPolicies.Login)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<SessionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Create(CreateSessionRequest request, CancellationToken cancellationToken)
    {
        var result = await login.ExecuteAsync(
            new LoginRequest(request.Email, request.Password, LoginChannel.CustomerApp), cancellationToken);

        return result.Tokens is { } tokens
            ? Ok(SessionResponse.From(tokens))
            : Problem(type: IdentityErrors.InvalidCredentials, title: IdentityErrors.InvalidCredentialsTitle, statusCode: StatusCodes.Status401Unauthorized);
    }
}
