using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ouroboros.Identity.Infrastructure.Persistence;

// Readiness de Identity depende somente do próprio banco e das tabelas migradas; nunca consulta o RabbitMQ.
internal sealed class IdentityReadinessHealthCheck(IOptions<IdentityDatabaseOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var connectionString = options.Value.ConnectionString;
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return HealthCheckResult.Unhealthy();
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(timeout.Token);
            await using var command = new NpgsqlCommand(
                "SELECT 1 FROM identity.parameters LIMIT 1; SELECT 1 FROM identity.email_intents LIMIT 1;",
                connection);
            command.CommandTimeout = 3;
            await command.ExecuteNonQueryAsync(timeout.Token);
            return HealthCheckResult.Healthy();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy();
        }
        catch (Exception)
        {
            return HealthCheckResult.Unhealthy();
        }
    }
}
