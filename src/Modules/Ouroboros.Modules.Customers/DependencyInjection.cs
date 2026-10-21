using Microsoft.Extensions.DependencyInjection;

namespace Ouroboros.Modules.Customers;

public static class DependencyInjection
{
    public static IServiceCollection AddCustomersModule(this IServiceCollection services)
    {
        // Registrar dependências e persistência quando os casos de uso forem definidos.
        return services;
    }
}
