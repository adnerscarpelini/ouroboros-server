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
[Route("api/v{version:apiVersion}/admin/identity/admin-invitations")]
public sealed class AdminInvitationsController(IInviteAdmin invite, ICompleteAdminInvitation complete) : ControllerBase
{
    [HttpPost]
    [Authorize(Policy = IdentityPolicies.AdminsInvite)]
    [EnableRateLimiting(IdentityRateLimitPolicies.AdminActions)]
    public async Task<IActionResult> Post(InviteAdminBody body, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirst(IdentityClaims.Subject)?.Value, out var actor)) { return Unauthorized(); }
        return Result(await invite.ExecuteAsync(new(actor, body.Email, body.CurrentPassword), cancellationToken));
    }

    [HttpPost("complete")]
    [AllowAnonymous]
    [EnableRateLimiting(IdentityRateLimitPolicies.RegistrationCompletion)]
    public async Task<IActionResult> Complete(CompleteAdminInvitationBody body, CancellationToken cancellationToken) =>
        Result(await complete.ExecuteAsync(new(body.Token, body.Password), cancellationToken));

    private IActionResult Result(AdminInvitationResult result) => result.Outcome switch
    {
        AdminInvitationOutcome.Accepted => Accepted(new { id = result.InvitationId }),
        AdminInvitationOutcome.Completed => NoContent(),
        AdminInvitationOutcome.Forbidden => Problem(statusCode: 403, title: "Ação não permitida."),
        AdminInvitationOutcome.InvalidCredentials => Problem(statusCode: 401, type: IdentityErrors.InvalidCredentials, title: IdentityErrors.InvalidCredentialsTitle),
        AdminInvitationOutcome.Conflict => Problem(statusCode: 409, title: "E-mail já cadastrado ou com convite pendente."),
        _ => Problem(statusCode: 400, title: "Convite inválido.", detail: result.Rule),
    };
}

public sealed record InviteAdminBody([Required, MaxLength(254)] string Email, [Required, MaxLength(1024)] string CurrentPassword);
public sealed record CompleteAdminInvitationBody([Required, MaxLength(256)] string Token, [Required, MaxLength(1024)] string Password);
