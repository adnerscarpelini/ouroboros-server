using System.ComponentModel.DataAnnotations;
using System.Net.Http.Headers;
using System.Text;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Ouroboros.Identity.Contracts;
using Ouroboros.Identity.Contracts.ApiClients;

namespace Ouroboros.Identity.Api.Controllers.External;

[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/identity/oauth")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class OAuthController(IClientCredentials credentials) : ControllerBase
{
    [HttpPost("token")]
    [AllowAnonymous]
    [Consumes("application/x-www-form-urlencoded")]
    [RequestSizeLimit(4096)]
    [EnableRateLimiting(IdentityRateLimitPolicies.Login)]
    public async Task<IActionResult> Token([FromForm] ClientTokenForm body, CancellationToken cancellationToken)
    {
        if (Request.Form.Any(field => field.Value.Count != 1)) { return Error(400, "invalid_request"); }
        if (!string.IsNullOrEmpty(body.Scope)) { return Error(400, "invalid_scope"); }
        var id = body.ClientId;
        var secret = body.ClientSecret;
        if (Request.Headers.ContainsKey("Authorization"))
        {
            if (Request.Form.ContainsKey("client_id") || Request.Form.ContainsKey("client_secret")
                || !AuthenticationHeaderValue.TryParse(Request.Headers.Authorization, out var authorization)
                || !authorization.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase)
                || authorization.Parameter is not { Length: > 0 and <= 2048 })
            { return Error(401, "invalid_client"); }
            try
            {
                var value = Encoding.UTF8.GetString(Convert.FromBase64String(authorization.Parameter));
                var separator = value.IndexOf(':');
                if (separator < 0) { return Error(401, "invalid_client"); }
                id = Uri.UnescapeDataString(value[..separator].Replace('+', ' '));
                secret = Uri.UnescapeDataString(value[(separator + 1)..].Replace('+', ' '));
            }
            catch (FormatException) { return Error(401, "invalid_client"); }
        }
        var result = await credentials.ExecuteAsync(new(body.GrantType, id, secret), cancellationToken);
        return result.Outcome switch
        {
            ClientCredentialsOutcome.Issued => Ok(new { access_token = result.AccessToken, token_type = "Bearer", expires_in = result.ExpiresIn }),
            ClientCredentialsOutcome.UnsupportedGrant => Error(400, "unsupported_grant_type"),
            _ => Error(401, "invalid_client"),
        };
    }

    private IActionResult Error(int status, string error)
    {
        if (status == 401) { Response.Headers.WWWAuthenticate = "Basic realm=\"ouroboros\""; }
        return Problem(statusCode: status, title: status == 401 ? IdentityErrors.InvalidCredentialsTitle : "Solicitação de token inválida.",
            extensions: new Dictionary<string, object?> { ["error"] = error });
    }
}
public sealed class ClientTokenForm
{
    [FromForm(Name = "grant_type"), Required, MaxLength(32)] public string GrantType { get; set; } = "";
    [FromForm(Name = "client_id"), MaxLength(128)] public string? ClientId { get; set; }
    [FromForm(Name = "client_secret"), MaxLength(256)] public string? ClientSecret { get; set; }
    [FromForm(Name = "scope"), MaxLength(256)] public string? Scope { get; set; }
}
