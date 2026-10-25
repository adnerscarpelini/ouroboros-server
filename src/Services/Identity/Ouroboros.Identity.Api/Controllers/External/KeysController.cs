using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Ouroboros.Identity.Contracts;

namespace Ouroboros.Identity.Api.Controllers.External;

[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/identity/keys")]
[AllowAnonymous]
[EnableRateLimiting(IdentityRateLimitPolicies.Keys)]
public sealed class KeysController(ITokenPublicKeys keys) : ControllerBase
{
    [HttpGet]
    public ActionResult<PublicKeySet> Get()
    {
        Response.Headers.CacheControl = "public, max-age=300, must-revalidate";
        return Ok(keys.Get());
    }
}
