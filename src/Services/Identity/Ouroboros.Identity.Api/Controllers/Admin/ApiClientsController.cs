using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Ouroboros.Identity.Contracts;
using Ouroboros.Identity.Contracts.ApiClients;

namespace Ouroboros.Identity.Api.Controllers.Admin;

[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/admin/identity/api-clients")]
[Authorize(Policy = IdentityPolicies.ApiClientsManage)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ApiClientsController(IRegisterApiClient register, IGetApiClient query) : ControllerBase
{
    [HttpPost]
    [EnableRateLimiting(IdentityRateLimitPolicies.AdminActions)]
    public async Task<IActionResult> Post(RegisterApiClientBody body, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirst(IdentityClaims.Subject)?.Value, out var actor)) { return Unauthorized(); }
        var result = await register.ExecuteAsync(new(actor, body.Name, body.Description, body.CurrentPassword), cancellationToken);
        return result.Outcome == ApiClientOutcome.Registered
            ? CreatedAtAction(nameof(Get), new { clientId = result.Client!.ClientId }, new { client_id = result.Client.ClientId, client_secret = result.Secret })
            : Failure(result.Outcome);
    }

    [HttpGet("{clientId:guid}")]
    public async Task<IActionResult> Get(Guid clientId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirst(IdentityClaims.Subject)?.Value, out var actor)) { return Unauthorized(); }
        var result = await query.ExecuteAsync(actor, clientId, cancellationToken);
        return result.Outcome == ApiClientOutcome.Found ? Ok(result.Client) : Failure(result.Outcome);
    }

    private IActionResult Failure(ApiClientOutcome outcome) => outcome switch
    {
        ApiClientOutcome.InvalidCredentials => Problem(statusCode: 401, type: IdentityErrors.InvalidCredentials, title: IdentityErrors.InvalidCredentialsTitle),
        ApiClientOutcome.Forbidden => Problem(statusCode: 403, title: "Ação não permitida."),
        ApiClientOutcome.NotFound => Problem(statusCode: 404, title: "Integrador não encontrado."),
        _ => Problem(statusCode: 400, title: "Dados do integrador inválidos."),
    };
}
public sealed record RegisterApiClientBody([Required, MaxLength(100)] string Name,
    [MaxLength(1000)] string? Description, [Required, MaxLength(1024)] string CurrentPassword);
