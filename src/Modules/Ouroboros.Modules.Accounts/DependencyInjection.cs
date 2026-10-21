using Microsoft.Extensions.DependencyInjection;

namespace Ouroboros.Modules.Accounts;

public static class DependencyInjection
{
    public static IServiceCollection AddAccountsModule(this IServiceCollection services)
    {
        // Registrar dependências e persistência quando os casos de uso forem definidos.
        return services;
    }
}
