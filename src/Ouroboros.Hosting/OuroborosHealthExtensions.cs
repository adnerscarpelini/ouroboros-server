using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ouroboros.Hosting;

// Endpoints técnicos de liveness e readiness comuns aos serviços. A resposta usa payload fixo e nunca
// serializa descrições, mensagens de exceção, connection strings ou dados internos das dependências.
public static class OuroborosHealthExtensions
{
    public const string LivePath = "/health/live";
    public const string ReadyPath = "/health/ready";
    public const string HealthPath = "/health";

    private static readonly byte[] HealthyPayload = """{"status":"Healthy"}"""u8.ToArray();
    private static readonly byte[] UnhealthyPayload = """{"status":"Unhealthy"}"""u8.ToArray();

    public static IHealthChecksBuilder AddOuroborosHealthChecks(this IServiceCollection services) =>
        services.AddHealthChecks();

    public static IEndpointRouteBuilder MapOuroborosHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        var live = new HealthCheckOptions
        {
            Predicate = _ => false,
            ResponseWriter = WriteSanitizedResponseAsync,
        };
        var ready = new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready"),
            ResponseWriter = WriteSanitizedResponseAsync,
        };

        endpoints.MapHealthChecks(LivePath, live).AllowAnonymous().DisableRateLimiting();
        endpoints.MapHealthChecks(ReadyPath, ready).AllowAnonymous().DisableRateLimiting();
        endpoints.MapHealthChecks(HealthPath, ready).AllowAnonymous().DisableRateLimiting();
        return endpoints;
    }

    private static Task WriteSanitizedResponseAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store, no-cache";
        var payload = report.Status == HealthStatus.Healthy ? HealthyPayload : UnhealthyPayload;
        return context.Response.Body.WriteAsync(payload, context.RequestAborted).AsTask();
    }
}
