using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ouroboros.Identity.Infrastructure.Persistence;

namespace Ouroboros.Identity.Infrastructure.Messaging;

internal sealed partial class EmailIntentMaintenance(IServiceScopeFactory scopes, IOptions<EmailIntentOptions> options,
    TimeProvider clock, ILogger<EmailIntentMaintenance> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.MaintenanceEnabled) { return; }
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), clock);
        do
        {
            try { EmailIntentBackupRetention.Purge(options.Value.BackupDirectory, clock.GetUtcNow()); }
            catch (Exception) { LogBackupCleanupFailed(logger); }
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await EmailIntentPersistence.MaintainAsync(scope.ServiceProvider.GetRequiredService<IdentityDbContext>(), clock.GetUtcNow(), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception) { LogCleanupFailed(logger); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Limpeza de intenções de e-mail não concluída; retomada no próximo ciclo.")]
    private static partial void LogCleanupFailed(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Limpeza de backups protegidos não concluída; retomada no próximo ciclo.")]
    private static partial void LogBackupCleanupFailed(ILogger logger);
}
