using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using Ouroboros.Identity.Infrastructure.Persistence;
using Ouroboros.Notifications.Infrastructure;

namespace Ouroboros.IntegrationTests;

// ClearAllPools é global ao processo; esta coleção impede interferência das limpezas de outras fixtures.
[Collection("Telemetry")]
public sealed class ReadinessPoolIsolationTests(ProvisionedInstance database, RabbitMqFixture broker)
    : IClassFixture<ProvisionedInstance>, IClassFixture<RabbitMqFixture>
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, "unreachable")]
    [InlineData(false, "credentials")]
    [InlineData(false, "database")]
    [InlineData(true, "unreachable")]
    [InlineData(true, "credentials")]
    [InlineData(true, "database")]
    public async Task Failed_database_probe_preserves_healthy_pool_and_recovers(bool notifications, string failure)
    {
        await MigrateAsync();
        var healthy = ConnectionString(notifications);
        using var pool = new HealthyPool(healthy);
        var backend = await pool.BackendAsync();
        var failed = new NpgsqlConnectionStringBuilder(healthy);
        switch (failure)
        {
            case "unreachable": failed.Host = "127.0.0.1"; failed.Port = 1; break;
            case "credentials": failed.Password = "invalid-readiness-test-password"; break;
            case "database": failed.Database = ProvisionedInstance.LegacyDatabase; break;
        }
        var identity = new IdentityDatabaseOptions { ConnectionString = failed.ConnectionString };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Notifications"] = failed.ConnectionString,
        }).Build();
        IHealthCheck probe = notifications ? NotificationsProbe(configuration) : new IdentityReadinessHealthCheck(Options.Create(identity));
        var started = Stopwatch.GetTimestamp();

        AssertSanitized(await probe.CheckHealthAsync(new HealthCheckContext(), Cancellation), HealthStatus.Unhealthy);
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(6));
        Assert.Equal(backend, await pool.BackendAsync());

        // Corrige a dependência da mesma probe, sem reiniciar o serviço ou invalidar pools.
        identity.ConnectionString = healthy;
        configuration["ConnectionStrings:Notifications"] = healthy;
        AssertSanitized(await probe.CheckHealthAsync(new HealthCheckContext(), Cancellation), HealthStatus.Healthy);
        Assert.Equal(backend, await pool.BackendAsync());
    }

    [Fact]
    public async Task Broker_outage_does_not_clear_database_pool_and_notifications_recovers()
    {
        await MigrateAsync();
        using var pool = new HealthyPool(ConnectionString(notifications: true));
        var backend = await pool.BackendAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Notifications"] = ConnectionString(notifications: true),
        }).Build();
        var probe = NotificationsProbe(configuration);
        AssertSanitized(await probe.CheckHealthAsync(new HealthCheckContext(), Cancellation), HealthStatus.Healthy);
        try
        {
            await broker.StopAsync();
            var started = Stopwatch.GetTimestamp();
            AssertSanitized(await probe.CheckHealthAsync(new HealthCheckContext(), Cancellation), HealthStatus.Unhealthy);
            Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(6));
            Assert.Equal(backend, await pool.BackendAsync());
            var identity = new IdentityReadinessHealthCheck(Options.Create(new IdentityDatabaseOptions
            {
                ConnectionString = ConnectionString(notifications: false),
            }));
            AssertSanitized(await identity.CheckHealthAsync(new HealthCheckContext(), Cancellation), HealthStatus.Healthy);
        }
        finally { await broker.StartAsync(); }
        AssertSanitized(await probe.CheckHealthAsync(new HealthCheckContext(), Cancellation), HealthStatus.Healthy);
        Assert.Equal(backend, await pool.BackendAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelled_probe_preserves_pool_and_next_probe_is_healthy(bool notifications)
    {
        await MigrateAsync();
        var healthy = ConnectionString(notifications);
        using var pool = new HealthyPool(healthy);
        var backend = await pool.BackendAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Notifications"] = healthy,
        }).Build();
        IHealthCheck probe = notifications ? NotificationsProbe(configuration)
            : new IdentityReadinessHealthCheck(Options.Create(new IdentityDatabaseOptions { ConnectionString = healthy }));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        AssertSanitized(await probe.CheckHealthAsync(new HealthCheckContext(), cancelled.Token), HealthStatus.Unhealthy);
        Assert.Equal(backend, await pool.BackendAsync());
        AssertSanitized(await probe.CheckHealthAsync(new HealthCheckContext(), Cancellation), HealthStatus.Healthy);
        Assert.Equal(backend, await pool.BackendAsync());
    }

    private async Task MigrateAsync()
    {
        await Ouroboros.Identity.DependencyInjection.MigrateDatabaseAsync(
            database.ConnectionStringFor(ProvisionedInstance.IdentityMigrator, ProvisionedInstance.IdentityDatabase), Cancellation);
        await Ouroboros.Notifications.DependencyInjection.MigrateDatabaseAsync(
            database.ConnectionStringFor(ProvisionedInstance.NotificationsMigrator, ProvisionedInstance.NotificationsDatabase), Cancellation);
        Assert.Equal(0, (await database.ProvisionAsync()).ExitCode);
    }

    private string ConnectionString(bool notifications) => notifications
        ? database.ConnectionStringFor(ProvisionedInstance.NotificationsRuntime, ProvisionedInstance.NotificationsDatabase)
        : database.ConnectionStringFor(ProvisionedInstance.IdentityRuntime, ProvisionedInstance.IdentityDatabase);

    private NotificationsReadinessHealthCheck NotificationsProbe(IConfiguration configuration)
    {
        var settings = new NotificationOptions
        {
            Broker = new BrokerSettings { Host = broker.Host, Port = broker.Port, Password = broker.ConsumerPassword },
        };
        var environment = new HostApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
            EnvironmentName = Environments.Development,
        }).Environment;
        return new NotificationsReadinessHealthCheck(configuration, Options.Create(settings), environment);
    }

    private static void AssertSanitized(HealthCheckResult result, HealthStatus expected)
    {
        Assert.Equal(expected, result.Status);
        Assert.Null(result.Description);
        Assert.Null(result.Exception);
        Assert.Empty(result.Data);
    }

    private sealed class HealthyPool : IDisposable
    {
        private readonly string _connectionString;
        public HealthyPool(string connectionString) => _connectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = "readiness-pool-" + Guid.NewGuid().ToString("N"),
            MaxPoolSize = 1,
            ConnectionLifetime = 0,
        }.ConnectionString;

        internal async Task<int> BackendAsync()
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(Cancellation);
            await using var command = new NpgsqlCommand("SELECT pg_backend_pid()", connection);
            return (int)(await command.ExecuteScalarAsync(Cancellation))!;
        }

        public void Dispose()
        {
            using var connection = new NpgsqlConnection(_connectionString);
            NpgsqlConnection.ClearPool(connection);
        }
    }
}
