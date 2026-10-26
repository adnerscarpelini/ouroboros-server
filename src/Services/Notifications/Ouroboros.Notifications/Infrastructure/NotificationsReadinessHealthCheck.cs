using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using Ouroboros.Messaging;

namespace Ouroboros.Notifications.Infrastructure;

// Readiness de Notifications depende tanto do banco próprio quanto da conectividade e fila do RabbitMQ.
internal sealed class NotificationsReadinessHealthCheck(
    IConfiguration configuration,
    IOptions<NotificationOptions> options,
    IHostEnvironment environment) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = options.Value;
            var connectionString = configuration.GetConnectionString("Notifications");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return HealthCheckResult.Unhealthy();
            }

            if (settings.Consumer.Enabled
                && (!File.Exists(settings.PrivateKeyRingPath) || !File.Exists(settings.FingerprintKeyRingPath)))
            {
                return HealthCheckResult.Unhealthy();
            }

            var broker = settings.Broker;
            if (broker.User != "notifications_consumer" || string.IsNullOrWhiteSpace(broker.Password) || broker.Password.Length < 32)
            {
                return HealthCheckResult.Unhealthy();
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));

            await using (var dbConnection = new NpgsqlConnection(connectionString))
            {
                await dbConnection.OpenAsync(timeout.Token);
                await using var command = new NpgsqlCommand("SELECT 1 FROM notifications.inbox LIMIT 1;", dbConnection);
                command.CommandTimeout = 3;
                await command.ExecuteNonQueryAsync(timeout.Token);
            }

            var factory = RabbitConnectionFactory.Create(broker.Host, broker.Port, broker.User, broker.Password, environment.IsDevelopment());
            factory.AutomaticRecoveryEnabled = false;
            factory.RequestedConnectionTimeout = TimeSpan.FromSeconds(2);
            await using var brokerConnection = await factory.CreateConnectionAsync(timeout.Token);
            await using var channel = await brokerConnection.CreateChannelAsync(cancellationToken: timeout.Token);
            await channel.QueueDeclarePassiveAsync("notifications.email.v1", timeout.Token);

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
