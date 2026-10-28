using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Ouroboros.Identity.Contracts;
using Ouroboros.Identity.Contracts.ApiClients;

namespace Ouroboros.Identity.Infrastructure.Security;

internal sealed partial class IdentityBearerEvents(IValidateApiClient clients, IOptions<JwtOptions> options, ILogger<IdentityBearerEvents> logger) : JwtBearerEvents
{
    public override async Task TokenValidated(TokenValidatedContext context)
    {
        var principal = context.Principal!;
        var kind = principal.FindFirst(IdentityClaims.PrincipalType)?.Value;
        var algorithm = ((JsonWebToken)context.SecurityToken).Alg;
        if (kind == "client")
        {
            if (context.Scheme.Name != IdentityAuthenticationSchemes.Customer || algorithm != SecurityAlgorithms.RsaSha256
                || principal.FindAll(IdentityClaims.PrincipalType).Count() != 1 || principal.FindAll(IdentityClaims.Subject).Count() != 1
                || principal.HasClaim(claim => claim.Type is IdentityClaims.Role or IdentityClaims.Session)
                || !Guid.TryParse(principal.FindFirst(IdentityClaims.Subject)?.Value, out var id) || id == Guid.Empty)
            { context.Fail("Principal inválido."); return; }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.HttpContext.RequestAborted);
            timeout.CancelAfter(options.Value.ClientValidationTimeout);
            try
            {
                if (!await clients.ExecuteAsync(id, timeout.Token)) { context.Fail("Principal inválido."); }
            }
            catch (Exception exception)
            {
                if (!context.HttpContext.RequestAborted.IsCancellationRequested) { LogUnavailable(logger, exception); }
                context.Fail("Validação de integrador indisponível.");
            }
            return;
        }
        // Preserva exclusivamente a janela HS256 já configurada para usuários legados sem discriminador.
        if (principal.FindAll(IdentityClaims.PrincipalType).Skip(1).Any()
            || principal.FindAll(IdentityClaims.Subject).Count() != 1
            || principal.FindAll(IdentityClaims.Session).Count() != 1 || principal.FindAll(IdentityClaims.Role).Count() != 1
            || (kind != "user" && !(kind is null && algorithm == SecurityAlgorithms.HmacSha256))
            || !Guid.TryParse(principal.FindFirst(IdentityClaims.Subject)?.Value, out var subject) || subject == Guid.Empty
            || !Guid.TryParse(principal.FindFirst(IdentityClaims.Session)?.Value, out var session) || session == Guid.Empty
            || principal.FindFirst(IdentityClaims.Role)?.Value is not ("Customer" or "Admin"))
        { context.Fail("Principal inválido."); }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Validação autoritativa de integrador indisponível; acesso recusado.")]
    private static partial void LogUnavailable(ILogger logger, Exception exception);
}
