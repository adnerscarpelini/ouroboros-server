namespace Ouroboros.Identity.Contracts;

// Nomes das políticas por capacidade do módulo, usados pelos controllers dos hosts em
// [Authorize(Policy = ...)]. O mapeamento de papel para política fica na DI do módulo.
public static class IdentityPolicies
{
    public const string AdminsInvite = "identity:admins.invite";
    public const string ApiClientsManage = "identity:api-clients.manage";
    public const string ApiClientAccess = "identity:api-clients.access";
    public const string ParametersRead = "settings:parameters.read";
    public const string ParametersWrite = "settings:parameters.write";
}
