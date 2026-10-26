using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ouroboros.Messaging;
using Ouroboros.Messaging.Contracts;
using Ouroboros.Notifications.Infrastructure.Persistence;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Ouroboros.Notifications.Infrastructure;

internal sealed partial class FailureReferenceRelay(IServiceScopeFactory scopes, IOptions<NotificationOptions> options,
    IHostEnvironment environment, TimeProvider clock, ILogger<FailureReferenceRelay> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Consumer.Enabled) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { LogInterrupted(logger, exception); }
            await Task.Delay(TimeSpan.FromSeconds(options.Value.ProcessingPollSeconds), clock, stoppingToken);
        }
    }

    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<FailureDispatchStore>();
        var lease = Guid.CreateVersion7();
        var batch = await store.ClaimAsync(lease, clock.GetUtcNow(), cancellationToken);
        foreach (var row in batch)
        {
            if (!await store.IsOwnedAsync(row.Id, lease, clock.GetUtcNow(), cancellationToken)) { continue; }
            var remaining = row.RetainUntil - clock.GetUtcNow();
            if (remaining.TotalMilliseconds < 1 || row.LeaseUntil <= clock.GetUtcNow().AddSeconds(10)) { continue; }
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var settings = options.Value.FailuresBroker;
                var factory = RabbitConnectionFactory.Create(settings.Host, settings.Port, settings.User, settings.Password, environment.IsDevelopment());
                factory.AutomaticRecoveryEnabled = false;
                await using var connection = await factory.CreateConnectionAsync(timeout.Token);
                await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(true, true), timeout.Token);
                var reference = new MessageFailureReference(row.Id, row.MessageId, row.Code, row.OccurredAt, row.RetainUntil);
                var body = EnvelopeCodec.EncodeFailure(reference, row.OriginalExpiresAt);
                remaining = row.RetainUntil - clock.GetUtcNow();
                if (remaining.TotalMilliseconds < 1) { continue; }
                var expiration = Math.Floor(Math.Min(remaining.TotalMilliseconds, TimeSpan.FromDays(30).TotalMilliseconds));
                await channel.BasicPublishAsync("ouroboros.notification-failures", "email.failed.v1", true, new BasicProperties
                {
                    Persistent = true,
                    ContentType = "application/json",
                    Type = reference.Type,
                    AppId = "notifications",
                    MessageId = row.Id.ToString("D"),
                    Expiration = expiration.ToString("0", CultureInfo.InvariantCulture),
                }, body, timeout.Token);
                await store.ConfirmAsync(row.Id, lease, clock.GetUtcNow(), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                var suspended = ConfigurationFailure(exception);
                var delay = Math.Min(300, Math.Pow(2, Math.Min(row.Attempts, 9)) * (0.8 + Random.Shared.NextDouble() * 0.4));
                await store.RetryAsync(row.Id, lease, suspended, suspended ? null : clock.GetUtcNow().AddSeconds(delay), cancellationToken);
                LogRetry(logger, row.Id, row.MessageId, suspended, exception);
            }
        }
    }

    private static bool ConfigurationFailure(Exception exception)
    {
        var pending = new Stack<Exception>();
        pending.Push(exception);
        for (var visited = 0; visited < 32 && pending.Count > 0; visited++)
        {
            var current = pending.Pop();
            if (current is PublishException { IsReturn: true } or AuthenticationFailureException or System.Security.Authentication.AuthenticationException
                || current is OperationInterruptedException { ShutdownReason.ReplyCode: 403 or 404 or 405 or 406 or 530 }) { return true; }
            if (current is AggregateException aggregate) { foreach (var inner in aggregate.InnerExceptions.Take(16)) { pending.Push(inner); } }
            else if (current.InnerException is not null) { pending.Push(current.InnerException); }
        }
        return false;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Referência de falha {FailureId} de {MessageId} não publicada; suspensa: {Suspended}.")]
    private static partial void LogRetry(ILogger logger, Guid failureId, Guid? messageId, bool suspended, Exception exception);
    [LoggerMessage(Level = LogLevel.Error, Message = "Relay sanitizado interrompido; classificação durável preservada.")]
    private static partial void LogInterrupted(ILogger logger, Exception exception);
}
