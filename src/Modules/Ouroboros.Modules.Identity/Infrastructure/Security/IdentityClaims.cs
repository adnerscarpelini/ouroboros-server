namespace Ouroboros.Modules.Identity.Infrastructure.Security;

// Nomes curtos dos claims, sem o mapeamento para os URIs do .NET (MapInboundClaims = false).
internal static class IdentityClaims
{
    public const string Subject = "sub";
    public const string Role = "role";
    public const string Session = "sid";
}
