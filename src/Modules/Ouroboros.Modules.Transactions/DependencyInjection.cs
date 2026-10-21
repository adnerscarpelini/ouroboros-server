using Microsoft.Extensions.DependencyInjection;

namespace Ouroboros.Modules.Transactions;

public static class DependencyInjection
{
    public static IServiceCollection AddTransactionsModule(this IServiceCollection services)
    {
        // Registrar dependências e persistência quando os casos de uso forem definidos.
        return services;
    }
}
