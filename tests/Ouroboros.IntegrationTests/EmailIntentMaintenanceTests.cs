using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ouroboros.Identity.Infrastructure.Messaging;

namespace Ouroboros.IntegrationTests;

public sealed class EmailIntentMaintenanceTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    [Fact]
    public async Task Database_maintenance_failure_does_not_prevent_expired_backup_cleanup()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ouroboros-backup-retention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, ".email-intent-backups"), EmailIntentBackupRetention.Marker);
            var expired = Path.Combine(directory, $"email-intents-{DateTimeOffset.UtcNow.AddDays(-31):yyyyMMddHHmmss}-{Guid.NewGuid():N}.json");
            File.WriteAllText(expired, "expired-protected-backup");
            // Apenas o banco isolado deste teste perde a tabela; nenhuma indisponibilidade é simulada.
            await ExecuteAsync("DROP TABLE identity.email_intents");
            var logger = new FailureSignalLogger();
            using var maintenance = new EmailIntentMaintenance(Api.Services.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new EmailIntentOptions { MaintenanceEnabled = true, BackupDirectory = directory }), TimeProvider.System, logger);
            await maintenance.StartAsync(TestContext.Current.CancellationToken);
            try
            {
                await logger.Failure.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                var wait = Stopwatch.StartNew();
                while (File.Exists(expired) && wait.Elapsed < TimeSpan.FromSeconds(2))
                {
                    await Task.Delay(10, TestContext.Current.CancellationToken);
                }
                Assert.False(File.Exists(expired));
            }
            finally { await maintenance.StopAsync(CancellationToken.None); }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class FailureSignalLogger : ILogger<EmailIntentMaintenance>
    {
        internal TaskCompletionSource Failure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error) { Failure.TrySetResult(); }
        }
    }
}
