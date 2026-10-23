using Ouroboros.Modules.Identity.Contracts.Sessions;
using Ouroboros.Modules.Identity.Domain.Users;

namespace Ouroboros.Modules.Identity.Application.Sessions;

// Cada canal (host) só aceita o seu papel, no login e na renovação.
internal static class LoginChannels
{
    public static UserRole RoleOf(LoginChannel channel) => channel switch
    {
        LoginChannel.CustomerApp => UserRole.Customer,
        LoginChannel.Backoffice => UserRole.Admin,
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, "Canal de login desconhecido."),
    };
}
