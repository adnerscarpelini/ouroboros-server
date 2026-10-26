using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ouroboros.BuildingBlocks.Storage;
using Ouroboros.Notifications.Infrastructure.Persistence;

namespace Ouroboros.Notifications.Infrastructure;

internal sealed partial class InboxMaintenance(IServiceScopeFactory scopes, IOptions<NotificationOptions> options,
    IConfiguration configuration, TimeProvider clock, ILogger<InboxMaintenance> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), clock);
        do
        {
            try { ProtectedEmailBackupRetention.Purge(options.Value.BackupDirectory, clock.GetUtcNow()); }
            catch (Exception) { LogBackupFailed(logger); }
            if (!string.IsNullOrWhiteSpace(configuration.GetConnectionString("Notifications")))
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await InboxPersistence.MaintainAsync(scope.ServiceProvider.GetRequiredService<NotificationsDbContext>(), clock.GetUtcNow(), stoppingToken);
                    var context = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
                    var snapshot = await context.Inbox.Where(row => row.State == Domain.InboxState.Expired || row.State == Domain.InboxState.Cancelled || row.State == Domain.InboxState.Failed)
                        .GroupBy(row => row.State).Select(group => new { State = group.Key.ToString(), Count = group.LongCount() })
                        .ToDictionaryAsync(item => item.State, item => item.Count, stoppingToken);
                    snapshot["failures"] = await context.Failures.LongCountAsync(row => row.PublishedAt == null, stoppingToken);
                    scope.ServiceProvider.GetRequiredService<InboxMetrics>().Snapshot(snapshot);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception) { LogDatabaseFailed(logger); }
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Limpeza de backups de Notifications não concluída; retomada no próximo ciclo.")]
    private static partial void LogBackupFailed(ILogger logger);
    [LoggerMessage(Level = LogLevel.Error, Message = "Limpeza da inbox não concluída; retomada no próximo ciclo.")]
    private static partial void LogDatabaseFailed(ILogger logger);
}
