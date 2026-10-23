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
public sealed class SessionsController(ILogin login, IRefreshSession refreshSession) : ControllerBase
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

    // Troca o refresh token por um par novo, na mesma sessão. Só renova sessões de usuários Customer.
    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting(IdentityRateLimitPolicies.Refresh)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<SessionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Refresh(RefreshSessionBody request, CancellationToken cancellationToken)
    {
        var result = await refreshSession.ExecuteAsync(
            new RefreshSessionRequest(request.RefreshToken, LoginChannel.CustomerApp), cancellationToken);

        return result.Tokens is { } tokens
            ? Ok(SessionResponse.From(tokens))
            : Problem(type: IdentityErrors.InvalidRefreshToken, title: IdentityErrors.InvalidRefreshTokenTitle, statusCode: StatusCodes.Status401Unauthorized);
    }
}
