using MudBlazor.Services;
using Ouroboros.Observability;
using Ouroboros.WebClient.Components;
using Ouroboros.WebClient.Features.Identity;
using Ouroboros.WebClient.Infrastructure;

namespace Ouroboros.WebClient;

// Composição do web client: infraestrutura de acesso ao gateway, uma chamada Add<Serviço>Feature() por serviço do
// backend com telas, componentes MudBlazor e renderização Interactive Server.
internal static class WebClientHost
{
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ApplicationName = typeof(WebClientEntryPoint).Assembly.GetName().Name,
        });
        builder.AddOuroborosObservability("web-client", typeof(WebClientEntryPoint).Assembly);

        builder.Services.AddWebClientInfrastructure(builder.Configuration);
        builder.Services.AddIdentityFeature();
        builder.Services.AddMudServices();
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        return builder.Build();
    }

    public static WebApplication UsePipeline(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/error", createScopeForErrors: true);
        }

        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
        app.UseAntiforgery();
        app.MapWebClientHealthChecks();
        app.MapStaticAssets();
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
        return app;
    }
}
