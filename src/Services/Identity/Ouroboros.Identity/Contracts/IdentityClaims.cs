namespace Ouroboros.Identity.Contracts;

// Nomes curtos dos claims do access token, sem o mapeamento para os URIs do .NET
// (MapInboundClaims = false). Públicos para os controllers dos hosts lerem o usuário autenticado.
public static class IdentityClaims
{
    public const string PrincipalType = "principal_type";
    public const string Subject = "sub";
    public const string Role = "role";
    public const string Session = "sid";
}
