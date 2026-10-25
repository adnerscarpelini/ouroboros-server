using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Ouroboros.Hosting;
using Ouroboros.Identity.Api.Listeners;
using Ouroboros.Identity.Api.Routing;
using Ouroboros.Identity.Api.Security;
using Ouroboros.Identity.Contracts;

namespace Ouroboros.Identity.Api;

// Composição da API de Identity: um processo, dois públicos separados por listener. O Program.cs só chama
// Build, executa um comando de operação (create-admin) ou sobe o servidor.
public static class IdentityApiHost
{
    private const string ListenerScheme = "Identity.Listener";

    // O configure é um ponto de extensão dos testes (ambiente, configuração, listeners em portas livres).
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        // O nome da aplicação é o desta API (de onde vêm os controllers), mesmo quando outro executável a hospeda.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ApplicationName = typeof(IdentityApiEntryPoint).Assembly.GetName().Name,
        });
        configure?.Invoke(builder);
        builder.AddOuroborosHost(HostAudience.Service);
        builder.Services.AddIdentityModule();
        AddListeners(builder);
        ForwardedHeadersSetup.Add(builder.Services);
        return builder.Build();
    }

    public static WebApplication UsePipeline(this WebApplication app)
    {
        ForwardedHeadersSetup.Use(app);

        // Antes do rate limiter e da autenticação: o escopo separa os contadores por público.
        app.Use((context, next) =>
        {
            var role = context.RequestServices.GetRequiredService<IListenerRoleResolver>().Resolve(context);
            context.Items[IdentityHttpItems.RateLimitScope] = role.ToString();
            return next(context);
        });

        return app.UseOuroborosHost();
    }

    private static void AddListeners(WebApplicationBuilder builder)
    {
        var services = builder.Services;
        services.AddSingleton<IValidateOptions<ListenerOptions>, ListenerOptionsValidator>();
        services.AddOptions<ListenerOptions>().BindConfiguration(ListenerOptions.SectionName).ValidateOnStart();
        services.TryAddSingleton<IListenerRoleResolver, ConnectionListenerRoleResolver>();
        services.AddSingleton<IValidateOptions<ApiAudienceSettings>, ApiAudienceSettingsValidator>();
        services.AddOptions<ApiAudienceSettings>().BindConfiguration(ApiAudienceSettings.SectionName).ValidateOnStart();

        // Só o servidor Kestrel real lê estes listeners; um TestServer ignora esta configuração.
        builder.WebHost.ConfigureKestrel((_, kestrel) =>
            KestrelListeners.Configure(kestrel, kestrel.ApplicationServices.GetRequiredService<IOptions<ListenerOptions>>().Value));

        services.Configure<MvcOptions>(options => options.Conventions.Add(new ListenerRouteConvention()));
        services.AddSingleton<MatcherPolicy, ListenerEndpointSelectorPolicy>();

        // Cada listener aceita só a audiência do próprio público: a escolha do esquema vem do listener.
        services.AddAuthentication(ListenerScheme)
            .AddPolicyScheme(ListenerScheme, displayName: null, options => options.ForwardDefaultSelector = context =>
                context.RequestServices.GetRequiredService<IListenerRoleResolver>().Resolve(context) == ListenerRole.Admin
                    ? IdentityAuthenticationSchemes.Admin
                    : IdentityAuthenticationSchemes.Customer);
    }
}
