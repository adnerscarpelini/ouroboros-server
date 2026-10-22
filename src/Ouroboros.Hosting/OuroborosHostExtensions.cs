using System.Diagnostics;
using Asp.Versioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;

namespace Ouroboros.Hosting;

// Configuração HTTP comum aos dois hosts. Cada host continua registrando seus módulos no próprio
// Program.cs, então a composição de cada um fica visível ali.
public static class OuroborosHostExtensions
{
    public static WebApplicationBuilder AddOuroborosHost(this WebApplicationBuilder builder, HostAudience audience)
    {
        builder.Services.AddSingleton(new CurrentHost(audience));

        builder.Services.AddControllers().ConfigureApplicationPartManager(manager =>
        {
            manager.FeatureProviders.Remove(manager.FeatureProviders.OfType<ControllerFeatureProvider>().Single());
            manager.FeatureProviders.Add(new AudienceControllerFeatureProvider(audience));
        });

        // A versão maior vem sempre na URL (api/v{version:apiVersion}/...) e não há versão implícita.
        builder.Services.AddApiVersioning(options =>
        {
            options.ApiVersionReader = new UrlSegmentApiVersionReader();
            options.ReportApiVersions = true;
        }).AddMvc();

        // Todo erro sai em application/problem+json (RFC 9457) com o trace id W3C da requisição, o
        // mesmo dos logs. Falhas de negócio definem o próprio type nos controllers dos módulos.
        builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
            context.ProblemDetails.Extensions["traceId"] = Activity.Current?.Id ?? context.HttpContext.TraceIdentifier);

        return builder;
    }

    public static WebApplication UseOuroborosHost(this WebApplication app)
    {
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.MapControllers();
        return app;
    }
}
