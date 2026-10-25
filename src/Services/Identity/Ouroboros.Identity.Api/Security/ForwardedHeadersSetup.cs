using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ouroboros.Identity.Api.Security;

// Cabeçalhos X-Forwarded-* só valem de proxies e redes conhecidos e configurados em Identity:ForwardedHeaders
// (KnownProxies e KnownNetworks). Sem configuração o middleware nem entra no pipeline, e o rate limit por IP usa
// o endereço da conexão: um cabeçalho forjado não troca o contador.
internal static class ForwardedHeadersSetup
{
    public static bool IsConfigured(IConfiguration configuration)
    {
        var section = configuration.GetSection(ForwardedHeadersSettings.SectionName).Get<ForwardedHeadersSettings>();
        return section is { KnownProxies.Length: > 0 } or { KnownNetworks.Length: > 0 };
    }

    // A configuração é lida quando as opções são resolvidas, depois de todas as fontes carregadas.
    public static void Add(IServiceCollection services)
    {
        services.AddSingleton<IValidateOptions<ForwardedHeadersSettings>, ForwardedHeadersSettingsValidator>();
        services.AddOptions<ForwardedHeadersSettings>().BindConfiguration(ForwardedHeadersSettings.SectionName).ValidateOnStart();

        services.AddOptions<ForwardedHeadersOptions>().Configure<IOptions<ForwardedHeadersSettings>>((options, settings) =>
        {
            if (settings.Value is { KnownProxies.Length: 0, KnownNetworks.Length: 0 })
            {
                return;
            }

            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;

            // Os padrões do ASP.NET Core confiam em loopback; aqui só vale o que foi configurado.
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            foreach (var proxy in settings.Value.KnownProxies)
            {
                options.KnownProxies.Add(IPAddress.Parse(proxy));
            }

            foreach (var network in settings.Value.KnownNetworks)
            {
                options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
            }
        });
    }

    public static void Use(WebApplication app)
    {
        if (IsConfigured(app.Configuration))
        {
            app.UseForwardedHeaders();
        }
    }
}

internal sealed class ForwardedHeadersSettings
{
    public const string SectionName = "Identity:ForwardedHeaders";

    public string[] KnownProxies { get; set; } = [];

    public string[] KnownNetworks { get; set; } = [];
}

internal sealed class ForwardedHeadersSettingsValidator : IValidateOptions<ForwardedHeadersSettings>
{
    public ValidateOptionsResult Validate(string? name, ForwardedHeadersSettings options)
    {
        var failures = new List<string>();
        failures.AddRange(options.KnownProxies
            .Where(proxy => !IPAddress.TryParse(proxy, out _))
            .Select(proxy => $"'{ForwardedHeadersSettings.SectionName}:KnownProxies' contém um IP inválido: '{proxy}'."));
        failures.AddRange(options.KnownNetworks
            .Where(network => !System.Net.IPNetwork.TryParse(network, out _))
            .Select(network => $"'{ForwardedHeadersSettings.SectionName}:KnownNetworks' contém uma rede inválida: '{network}'. Use CIDR, por exemplo 10.0.0.0/8."));
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
