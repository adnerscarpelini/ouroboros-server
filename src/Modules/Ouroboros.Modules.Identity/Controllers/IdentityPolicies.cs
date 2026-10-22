using Microsoft.AspNetCore.Authorization;
using Ouroboros.Modules.Identity.Domain.Users;

namespace Ouroboros.Modules.Identity.Controllers;

// Políticas por capacidade do módulo. O mapeamento de papel para política fica só aqui; as actions
// declaram a capacidade, não o papel.
internal static class IdentityPolicies
{
    public const string AdminsInvite = "identity:admins.invite";

    public static void Configure(AuthorizationOptions options) =>
        options.AddPolicy(AdminsInvite, policy => policy.RequireRole(nameof(UserRole.Admin)));
}
