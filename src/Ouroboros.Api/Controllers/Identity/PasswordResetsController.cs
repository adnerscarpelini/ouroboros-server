using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Ouroboros.Modules.Identity.Contracts;
using Ouroboros.Modules.Identity.Contracts.PasswordResets;

namespace Ouroboros.Api.Controllers.Identity;

// Recuperação de senha de clientes: um link de uso único vai para o e-mail da conta.
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/identity/password-resets")]
public sealed class PasswordResetsController(IRequestPasswordReset requestPasswordReset) : ControllerBase
{
    // Sempre 202 com a mesma mensagem, exista ou não uma conta com o e-mail.
    [HttpPost]
    [AllowAnonymous]
    [EnableRateLimiting(IdentityRateLimitPolicies.PasswordReset)]
    [ProducesResponseType<AcceptedResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Create(PasswordResetRequestBody request, CancellationToken cancellationToken)
    {
        var result = await requestPasswordReset.ExecuteAsync(new RequestPasswordResetRequest(request.Email), cancellationToken);
        if (result == RequestPasswordResetResult.InvalidEmail)
        {
            ModelState.AddModelError(nameof(PasswordResetRequestBody.Email), "Informe um e-mail válido.");
            return ValidationProblem(ModelState);
        }

        return Accepted(new AcceptedResponse(
            "Se houver uma conta com esse e-mail, você receberá um link para redefinir a senha."));
    }
}

// Só o e-mail; o formato é conferido pela regra estrita do módulo.
public sealed record PasswordResetRequestBody([Required, MaxLength(254)] string Email);
