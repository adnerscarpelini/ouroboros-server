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
public sealed class PasswordResetsController(
    IRequestPasswordReset requestPasswordReset,
    ICompletePasswordReset completePasswordReset) : ControllerBase
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

    // Define a senha nova com o token do link. Encerra todas as sessões e não faz login.
    [HttpPost("complete")]
    [AllowAnonymous]
    [EnableRateLimiting(IdentityRateLimitPolicies.PasswordResetCompletion)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Complete(CompletePasswordResetBody request, CancellationToken cancellationToken)
    {
        var result = await completePasswordReset.ExecuteAsync(
            new CompletePasswordResetRequest(request.Token, request.Password), cancellationToken);

        return result.Outcome switch
        {
            CompletePasswordResetOutcome.Completed => NoContent(),
            CompletePasswordResetOutcome.PasswordRejected => Problem(
                type: IdentityErrors.PasswordRejected,
                title: IdentityErrors.PasswordRejectedTitle,
                detail: result.Message,
                statusCode: StatusCodes.Status400BadRequest),
            _ => Problem(
                type: IdentityErrors.InvalidPasswordResetToken,
                title: IdentityErrors.InvalidPasswordResetTokenTitle,
                statusCode: StatusCodes.Status400BadRequest),
        };
    }
}

// Token e senha ausentes ou vazios recebem a resposta do caso de uso. Os limites de tamanho só protegem
// o servidor de entradas enormes.
public sealed record CompletePasswordResetBody([MaxLength(256)] string? Token, [MaxLength(1024)] string? Password);

// Só o e-mail; o formato é conferido pela regra estrita do módulo.
public sealed record PasswordResetRequestBody([Required, MaxLength(254)] string Email);
