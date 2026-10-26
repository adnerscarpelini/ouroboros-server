using Microsoft.AspNetCore.Builder;
using Ouroboros.Hosting.Gateway;
using Ouroboros.Observability;

namespace Ouroboros.ExternalGateway.Api;

public static class ExternalGatewayHost
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ApplicationName = typeof(ExternalGatewayEntryPoint).Assembly.GetName().Name,
        });
        builder.AddOuroborosObservability("external-gateway", typeof(ExternalGatewayEntryPoint).Assembly);
        configure?.Invoke(builder);
        builder.AddOuroborosGateway(GatewayAudience.External);
        return builder.Build();
    }

    public static WebApplication UsePipeline(this WebApplication app) =>
        app.UseOuroborosGateway();
}
