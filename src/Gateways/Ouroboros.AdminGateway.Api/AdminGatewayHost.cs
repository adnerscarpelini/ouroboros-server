using Microsoft.AspNetCore.Builder;
using Ouroboros.Hosting.Gateway;
using Ouroboros.Observability;

namespace Ouroboros.AdminGateway.Api;

public static class AdminGatewayHost
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ApplicationName = typeof(AdminGatewayEntryPoint).Assembly.GetName().Name,
        });
        builder.AddOuroborosObservability("admin-gateway", typeof(AdminGatewayEntryPoint).Assembly);
        configure?.Invoke(builder);
        builder.AddOuroborosGateway(GatewayAudience.Admin);
        return builder.Build();
    }

    public static WebApplication UsePipeline(this WebApplication app) =>
        app.UseOuroborosGateway();
}
