using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Ouroboros.WebClient.Infrastructure;

internal static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddWebClientInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IValidateOptions<WebClientOptions>, WebClientOptionsValidator>();
        services.AddOptions<WebClientOptions>().BindConfiguration(WebClientOptions.SectionName).ValidateOnStart();

        // O HttpClient propaga o traceparent da requisição atual, ligando a interface aos logs do gateway e dos serviços.
        services.AddHttpClient(GatewayClient.Name, (provider, client) =>
        {
            var gateway = provider.GetRequiredService<IOptions<WebClientOptions>>().Value.Gateway;
            client.BaseAddress = new Uri(gateway.BaseUrl!);
            client.Timeout = gateway.Timeout;
        });

        services.AddHealthChecks().AddCheck<GatewayReadinessHealthCheck>("gateway", tags: [WebClientHealthEndpoints.ReadyTag]);

        // As chaves protegem os cookies do web client; em container, ficam num volume para sobreviver a reinícios.
        var dataProtection = services.AddDataProtection().SetApplicationName("Ouroboros.WebClient");
        var keysPath = configuration[$"{WebClientOptions.SectionName}:DataProtection:KeysPath"];
        if (!string.IsNullOrWhiteSpace(keysPath))
        {
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
        }

        return services;
    }
}
