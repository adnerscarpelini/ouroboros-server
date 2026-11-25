using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ouroboros.WebClient.Infrastructure;

// Liveness e readiness no mesmo contrato dos serviços: payload fixo, sem descrições, exceções nem endereços internos.
// O web client não referencia Ouroboros.Hosting (mecanismos das APIs dos serviços), então mantém a própria versão.
internal static class WebClientHealthEndpoints
{
    public const string LivePath = "/health/live";
    public const string ReadyPath = "/health/ready";
    public const string HealthPath = "/health";
    public const string ReadyTag = "ready";

    private static readonly byte[] HealthyPayload = """{"status":"Healthy"}"""u8.ToArray();
    private static readonly byte[] UnhealthyPayload = """{"status":"Unhealthy"}"""u8.ToArray();

    public static IEndpointRouteBuilder MapWebClientHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        var live = new HealthCheckOptions { Predicate = _ => false, ResponseWriter = WriteSanitizedResponseAsync };
        var ready = new HealthCheckOptions { Predicate = check => check.Tags.Contains(ReadyTag), ResponseWriter = WriteSanitizedResponseAsync };

        endpoints.MapHealthChecks(LivePath, live).AllowAnonymous();
        endpoints.MapHealthChecks(ReadyPath, ready).AllowAnonymous();
        endpoints.MapHealthChecks(HealthPath, ready).AllowAnonymous();
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
