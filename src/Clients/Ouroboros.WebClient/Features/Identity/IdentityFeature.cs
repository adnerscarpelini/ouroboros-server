namespace Ouroboros.WebClient.Features.Identity;

// Ponto de composição das telas de Identity, que falam com /api/v1/identity pelo gateway. Cada funcionalidade
// registra aqui o próprio cliente HTTP e serviços de tela; a pasta não usa tipos de outro Features/<Serviço>.
internal static class IdentityFeature
{
    public static IServiceCollection AddIdentityFeature(this IServiceCollection services) => services;
}
