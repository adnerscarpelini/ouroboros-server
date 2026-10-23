namespace Ouroboros.Modules.Identity.Contracts;

// Nomes das políticas por capacidade do módulo, usados pelos controllers dos hosts em
// [Authorize(Policy = ...)]. O mapeamento de papel para política fica na DI do módulo.
public static class IdentityPolicies
{
    public const string AdminsInvite = "identity:admins.invite";
}
