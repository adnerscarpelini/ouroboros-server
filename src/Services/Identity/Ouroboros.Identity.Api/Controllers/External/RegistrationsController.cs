using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Ouroboros.Identity.Contracts;
using Ouroboros.Identity.Contracts.Registrations;

namespace Ouroboros.Identity.Api.Controllers.External;

// Cadastro de clientes: primeiro a posse do e-mail é provada por um link; depois a senha é definida.
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/identity/registrations")]
public sealed class RegistrationsController(
    IRequestRegistration requestRegistration,
    ICompleteRegistration completeRegistration) : ControllerBase
{
    // Sempre 202 com a mesma mensagem, exista ou não uma conta com o e-mail.
    [HttpPost]
    [AllowAnonymous]
    [EnableRateLimiting(IdentityRateLimitPolicies.Registration)]
    [ProducesResponseType<AcceptedResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Create(RegistrationRequestBody request, CancellationToken cancellationToken)
    {
        var result = await requestRegistration.ExecuteAsync(new RequestRegistrationRequest(request.Email), cancellationToken);
        if (result == RequestRegistrationResult.InvalidEmail)
        {
            // Mesmo formato do 400 de validação do [ApiController].
            ModelState.AddModelError(nameof(RegistrationRequestBody.Email), "Informe um e-mail válido.");
            return ValidationProblem(ModelState);
        }

        return Accepted(new AcceptedResponse(
            "Se o e-mail puder ser usado, você receberá um link para concluir o cadastro."));
    }

    // Define a senha com o token do link e cria a conta de cliente. Não faz login: o cliente entra depois.
    [HttpPost("complete")]
    [AllowAnonymous]
    [EnableRateLimiting(IdentityRateLimitPolicies.RegistrationCompletion)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Complete(CompleteRegistrationBody request, CancellationToken cancellationToken)
    {
        var result = await completeRegistration.ExecuteAsync(
            new CompleteRegistrationRequest(request.Token, request.Password), cancellationToken);

        return result.Outcome switch
        {
            CompleteRegistrationOutcome.Completed => NoContent(),
            CompleteRegistrationOutcome.PasswordRejected => Problem(
                type: IdentityErrors.PasswordRejected,
                title: IdentityErrors.PasswordRejectedTitle,
                detail: result.Message,
                statusCode: StatusCodes.Status400BadRequest),
            _ => Problem(
                type: IdentityErrors.InvalidRegistrationToken,
                title: IdentityErrors.InvalidRegistrationTokenTitle,
                statusCode: StatusCodes.Status400BadRequest),
        };
    }
}

// Só o e-mail: outros campos, como um papel, são ignorados. O formato é conferido pela regra estrita
// do módulo (o [EmailAddress] do ASP.NET Core só confere o @).
public sealed record RegistrationRequestBody([Required, MaxLength(254)] string Email);

// Token e senha ausentes ou vazios não são erro de validação: recebem a resposta do caso de uso. Os
// limites de tamanho só protegem o servidor de entradas enormes.
public sealed record CompleteRegistrationBody([MaxLength(256)] string? Token, [MaxLength(1024)] string? Password);

// Mensagem genérica das solicitações que não revelam se uma conta existe.
public sealed record AcceptedResponse(string Message);
