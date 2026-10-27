using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Ouroboros.Identity.Contracts;
using Ouroboros.Identity.Contracts.Sessions;
using Ouroboros.Identity.Contracts.Users;

namespace Ouroboros.Identity.Api.Controllers.External;

[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/identity/me/password")]
public sealed class PasswordController(IChangePassword changePassword) : ControllerBase
{
    [HttpPut]
    [EnableRateLimiting(IdentityRateLimitPolicies.PasswordChange)]
    public async Task<IActionResult> Put(ChangePasswordBody body, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirst(IdentityClaims.Subject)?.Value, out var userId)
            || !Guid.TryParse(User.FindFirst("sid")?.Value, out var sessionId))
        {
            return Unauthorized();
        }

        var result = await changePassword.ExecuteAsync(new(userId, sessionId, LoginChannel.CustomerApp, body.CurrentPassword, body.NewPassword), cancellationToken);
        return result.Outcome switch
        {
            ChangePasswordOutcome.Changed => NoContent(),
            ChangePasswordOutcome.PasswordRejected => Problem(statusCode: 400, type: IdentityErrors.PasswordRejected, title: IdentityErrors.PasswordRejectedTitle, detail: result.Rule),
            _ => Problem(statusCode: 401, type: IdentityErrors.InvalidCredentials, title: IdentityErrors.InvalidCredentialsTitle),
        };
    }
}

public sealed record ChangePasswordBody([Required, MaxLength(1024)] string CurrentPassword, [Required, MaxLength(1024)] string NewPassword);
