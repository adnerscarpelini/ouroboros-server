using System.Net;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Ouroboros.Hosting.Gateway;

internal sealed class GatewayReadinessHealthCheck(
    IOptions<GatewayOptions> options,
    IGatewayDownstreamClientFactory clientFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = options.Value;
            if (!GatewayOptionsValidator.TryValidateBaseUrl(settings.Downstream.IdentityBaseUrl, out var baseUri) || baseUri is null)
            {
                return HealthCheckResult.Unhealthy();
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));

            var readyUri = new Uri(baseUri, OuroborosHealthExtensions.ReadyPath);
            using var request = new HttpRequestMessage(HttpMethod.Get, readyUri)
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            };

            using var response = await clientFactory.CreateInvoker().SendAsync(request, timeoutCts.Token);
            return response.StatusCode == HttpStatusCode.OK
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy();
        }
        catch (Exception)
        {
            return HealthCheckResult.Unhealthy();
        }
    }
}
