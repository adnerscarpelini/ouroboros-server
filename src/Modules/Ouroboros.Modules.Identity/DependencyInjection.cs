using Microsoft.Extensions.DependencyInjection;

namespace Ouroboros.Modules.Identity;

public static class DependencyInjection
{
    public static IServiceCollection AddIdentityModule(this IServiceCollection services)
    {
        // Os controllers internal do módulo são descobertos a partir desta application part.
        services.AddControllers().AddApplicationPart(typeof(DependencyInjection).Assembly);

        // Registrar dependências e persistência quando os casos de uso forem definidos.
        return services;
    }
}
