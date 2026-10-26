using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ouroboros.Messaging;
using Ouroboros.Notifications.Application;
using Ouroboros.Notifications.Infrastructure.Persistence;

namespace Ouroboros.Notifications.Infrastructure;

internal sealed partial class InboxExecutor(IServiceScopeFactory scopes, IOptions<NotificationOptions> options,
    TimeProvider clock, ILogger<InboxExecutor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Consumer.Enabled) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception) { LogInterrupted(logger); }
            await Task.Delay(TimeSpan.FromSeconds(options.Value.ProcessingPollSeconds), clock, stoppingToken);
        }
    }

    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        await InboxPersistence.MaintainAsync(context, clock.GetUtcNow(), cancellationToken);
        var store = scope.ServiceProvider.GetRequiredService<InboxExecutionStore>();
        var lease = Guid.CreateVersion7();
        var row = (await store.ClaimAsync(lease, clock.GetUtcNow(), cancellationToken)).SingleOrDefault();
        if (row is null) { return; }
        var attempt = row.Attempts + 1;
        var decoded = EnvelopeCodec.TryDecode(row.ProtectedEnvelope!, "notification.email.requested", 1, "identity", out var message, out _);
        // Cada tentativa de entrega continua o trace da origem gravado no envelope aceito.
        using var activity = MessageTracing.Start(NotificationsTelemetry.Source, "inbox.deliver", ActivityKind.Consumer, decoded ? message : null);
        try
        {
            if (!decoded)
            {
                await store.FailAttemptAsync(row.MessageId, lease, "invalid-contract", clock.GetUtcNow(), clock.GetUtcNow(), cancellationToken);
                return;
            }
            var content = scope.ServiceProvider.GetRequiredService<IEmailPayloadReader>().Read(message!);
            var now = clock.GetUtcNow();
            if (!await store.IsOwnedAsync(row.MessageId, lease, now, cancellationToken)) { return; }
            var fallback = now.Add(RetryDelay(attempt));
            if (await store.BeginAttemptAsync(row.MessageId, lease, now, fallback < row.ExpiresAt ? fallback : row.ExpiresAt, cancellationToken) != 1) { return; }
            if (row.ExpiresAt <= clock.GetUtcNow() || row.LeaseUntil <= clock.GetUtcNow().AddSeconds(10)) { return; }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            // BeginAttempt é durável; nenhuma transação/lock de negócio continua aberto nesta chamada.
            await scope.ServiceProvider.GetRequiredService<IEmailTransport>().SendAsync(row.MessageId, content, timeout.Token);
            var recorded = await store.DeliverAsync(row.MessageId, lease, clock.GetUtcNow(), cancellationToken);
            LogResult(logger, row.MessageId, recorded == 1 ? "Delivered" : "ResultNotOwned");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (NotificationDeliveryException exception) when (exception.Kind == DeliveryFailure.Configuration)
        {
            var recorded = await store.SuspendAsync(row.MessageId, lease, clock.GetUtcNow(), cancellationToken);
            LogResult(logger, row.MessageId, recorded == 1 ? "Suspended" : "ResultNotOwned");
        }
        catch (Exception exception)
        {
            var now = clock.GetUtcNow();
            var permanent = exception is NotificationDeliveryException { Kind: DeliveryFailure.InvalidContent } ? "invalid-contract"
                : exception is NotificationDeliveryException { Kind: DeliveryFailure.Permanent } ? "provider-permanent" : null;
            var delay = RetryDelay(attempt);
            if (exception is NotificationDeliveryException { RetryAfter: { } retryAfter } && retryAfter > delay) { delay = retryAfter; }
            // Retry-After não pode estourar DateTimeOffset nem ampliar o prazo absoluto.
            var remaining = row.ExpiresAt - now;
            var retryAt = delay >= remaining ? row.ExpiresAt : now.Add(delay);
            await store.FailAttemptAsync(row.MessageId, lease, permanent, now, retryAt, cancellationToken);
            LogResult(logger, row.MessageId, permanent ?? "RetryOrExhausted");
        }
    }

    internal static TimeSpan RetryDelay(int attempt)
    {
        var seconds = attempt switch { <= 1 => 30, 2 => 120, 3 => 600, _ => 1800 };
        return TimeSpan.FromSeconds(seconds * (0.8 + Random.Shared.NextDouble() * 0.4));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Processamento de {MessageId}: {State}.")]
    private static partial void LogResult(ILogger logger, Guid messageId, string state);
    [LoggerMessage(Level = LogLevel.Error, Message = "Executor de Notifications interrompido; estado persistido será recuperado.")]
    private static partial void LogInterrupted(ILogger logger);
}
