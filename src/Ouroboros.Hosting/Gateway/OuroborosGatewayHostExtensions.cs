using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Ouroboros.Hosting.Gateway;

public static class OuroborosGatewayHostExtensions
{
    public static WebApplicationBuilder AddOuroborosGateway(this WebApplicationBuilder builder, GatewayAudience audience)
    {
        builder.AddOuroborosHost();
        builder.Services.AddRateLimiter();
        builder.Services.AddAuthorization(options => options.FallbackPolicy = null);

        builder.Services.AddSingleton(new GatewayAudienceDescriptor(audience));
        builder.Services.AddSingleton<IValidateOptions<GatewayOptions>>(new GatewayOptionsValidator(audience));
        builder.Services.AddOptions<GatewayOptions>()
            .BindConfiguration(GatewayOptions.SectionName)
            .ValidateOnStart();

        builder.Services.TryAddSingleton<IGatewayDownstreamClientFactory, DefaultGatewayDownstreamClientFactory>();
        builder.Services.AddSingleton<GatewayForwarder>();
        builder.Services.AddHealthChecks()
            .AddCheck<GatewayReadinessHealthCheck>("gateway-downstream-ready", tags: ["ready"]);

        builder.Services.AddOptions<ForwardedHeadersOptions>().Configure<IOptions<GatewayOptions>>((forwarded, gatewayOptions) =>
        {
            var settings = gatewayOptions.Value.ForwardedHeaders;
            if (settings is { KnownProxies.Length: 0, KnownNetworks.Length: 0 })
            {
                return;
            }

            forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            forwarded.ForwardLimit = 1;
            forwarded.KnownProxies.Clear();
            forwarded.KnownIPNetworks.Clear();
            foreach (var proxy in settings.KnownProxies)
            {
                forwarded.KnownProxies.Add(IPAddress.Parse(proxy));
            }

            foreach (var network in settings.KnownNetworks)
            {
                forwarded.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
            }
        });

        builder.WebHost.ConfigureKestrel((_, kestrel) =>
        {
            var options = kestrel.ApplicationServices.GetRequiredService<IOptions<GatewayOptions>>().Value;
            foreach (var rawUrl in options.Urls)
            {
                var uri = new Uri(rawUrl, UriKind.Absolute);
                void ConfigureListener(ListenOptions listener)
                {
                    if (uri.Scheme == Uri.UriSchemeHttps)
                    {
                        listener.UseHttps();
                    }
                }

                if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
                {
                    kestrel.ListenLocalhost(uri.Port, ConfigureListener);
                }
                else if (IPAddress.TryParse(uri.Host, out var address))
                {
                    kestrel.Listen(address, uri.Port, ConfigureListener);
                }
                else
                {
                    kestrel.ListenAnyIP(uri.Port, ConfigureListener);
                }
            }
        });

        return builder;
    }

    public static WebApplication UseOuroborosGateway(this WebApplication app)
    {
        var section = app.Configuration.GetSection($"{GatewayOptions.SectionName}:ForwardedHeaders").Get<GatewayForwardedHeadersOptions>();
        if (section is { KnownProxies.Length: > 0 } or { KnownNetworks.Length: > 0 })
        {
            app.UseForwardedHeaders();
        }

        app.UseOuroborosHost();

        var audience = app.Services.GetRequiredService<GatewayAudienceDescriptor>().Audience;
        var prefix = audience == GatewayAudience.Admin ? "/api/v1/admin/identity" : "/api/v1/identity";

        app.Map(prefix, (HttpContext context, GatewayForwarder forwarder) => forwarder.ForwardAsync(context))
            .AllowAnonymous()
            .DisableRateLimiting();
        app.Map($"{prefix}/{{**remainder}}", (HttpContext context, GatewayForwarder forwarder) => forwarder.ForwardAsync(context))
            .AllowAnonymous()
            .DisableRateLimiting();

        return app;
    }
}
