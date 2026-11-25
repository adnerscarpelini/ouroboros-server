using System.Net;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ouroboros.WebClient.Infrastructure;

// O web client está pronto quando o gateway externo está pronto: sem ele, nenhuma tela funciona. A prontidão do
// gateway já reflete a do serviço proprietário. O resultado não carrega descrição nem exceção.
internal sealed class GatewayReadinessHealthCheck(IHttpClientFactory clientFactory) : IHealthCheck
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);

            using var client = clientFactory.CreateClient(GatewayClient.Name);
            using var response = await client.GetAsync(WebClientHealthEndpoints.ReadyPath, timeout.Token);
            return response.StatusCode == HttpStatusCode.OK ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy();
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or IOException)
        {
            return HealthCheckResult.Unhealthy();
        }
    }
}
