using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Ouroboros.Identity.Contracts;
using Ouroboros.Identity.Contracts.Administrators;

namespace Ouroboros.Identity.Api.Controllers.Admin;

[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/admin/identity/admin-invitations/{id:guid}")]
[Authorize(Policy = IdentityPolicies.AdminsInvite)]
[EnableRateLimiting(IdentityRateLimitPolicies.AdminActions)]
public sealed class AdminInvitationDecisionsController(IDecideAdminInvitation decide) : ControllerBase
{
    [HttpPost("approval")]
    public Task<IActionResult> Approve(Guid id, InvitationDecisionBody body, CancellationToken cancellationToken) => DecideAsync(id, true, body, cancellationToken);
    [HttpPost("rejection")]
    public Task<IActionResult> Reject(Guid id, InvitationDecisionBody body, CancellationToken cancellationToken) => DecideAsync(id, false, body, cancellationToken);

    private async Task<IActionResult> DecideAsync(Guid id, bool approve, InvitationDecisionBody body, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirst(IdentityClaims.Subject)?.Value, out var actor)) { return Unauthorized(); }
        var result = await decide.ExecuteAsync(new(actor, id, approve, body.CurrentPassword), cancellationToken);
        return result.Outcome switch
        {
            AdminInvitationOutcome.Decided => NoContent(),
            AdminInvitationOutcome.InvalidCredentials => Problem(statusCode: 401, type: IdentityErrors.InvalidCredentials, title: IdentityErrors.InvalidCredentialsTitle),
            AdminInvitationOutcome.Forbidden => Problem(statusCode: 403, title: "Aprovador não autorizado."),
            AdminInvitationOutcome.NotFound => Problem(statusCode: 404, title: "Convite não encontrado."),
            AdminInvitationOutcome.Conflict => Problem(statusCode: 409, title: "Convite já decidido."),
            _ => Problem(statusCode: 400, title: "Convite expirado ou inválido."),
        };
    }
}
public sealed record InvitationDecisionBody([Required, MaxLength(1024)] string CurrentPassword);
