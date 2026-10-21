using Microsoft.Extensions.DependencyInjection;

namespace Ouroboros.Modules.Identity;

public static class DependencyInjection
{
    public static IServiceCollection AddIdentityModule(this IServiceCollection services)
    {
        // Registrar dependências e persistência quando os casos de uso forem definidos.
        return services;
    }
}
