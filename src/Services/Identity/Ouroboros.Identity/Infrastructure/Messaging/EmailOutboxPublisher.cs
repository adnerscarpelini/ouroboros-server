using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ouroboros.Identity.Infrastructure.Persistence;
using Ouroboros.Messaging;
using RabbitMQ.Client.Exceptions;

namespace Ouroboros.Identity.Infrastructure.Messaging;

internal sealed partial class EmailOutboxPublisher(IServiceScopeFactory scopes, IOptions<EmailPublisherOptions> options,
    IHostEnvironment environment, TimeProvider clock, EmailOutboxMetrics metrics, ILogger<EmailOutboxPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { LogCycleFailed(logger, exception); }
            await Task.Delay(TimeSpan.FromSeconds(options.Value.PollSeconds), clock, stoppingToken);
        }
    }

    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var lease = Guid.CreateVersion7();
        await using var scope = scopes.CreateAsyncScope();
        await EmailIntentPersistence.MaintainAsync(scope.ServiceProvider.GetRequiredService<IdentityDbContext>(), clock.GetUtcNow(), cancellationToken);
        var store = scope.ServiceProvider.GetRequiredService<EmailOutboxStore>();
        var batch = await store.ClaimAsync(lease, clock.GetUtcNow(), options.Value.BatchSize, cancellationToken);
        using var cycle = MessageTracing.StartBatch(OutboxTelemetry.Source, batch.Select(intent => (intent.Traceparent, intent.Tracestate)));
        MessageTracing.Describe(cycle, "publish", null);
        cycle?.SetTag("messaging.batch.message_count", batch.Count);
        await using var publisher = new ConfirmedEmailPublisher(options, environment);
        foreach (var intent in batch)
        {
            // Os logs da publicação continuam o trace da requisição que gravou a intenção.
            using var activity = MessageTracing.Start(OutboxTelemetry.Source, "outbox.publish", ActivityKind.Producer, intent.Traceparent, intent.Tracestate);
            MessageTracing.Describe(activity, "publish", intent.ExternalId);
            if (!await store.IsOwnedAsync(intent.Id, lease, clock.GetUtcNow(), cancellationToken)) { continue; }
            if (intent.ExpiresAt <= clock.GetUtcNow() || intent.LeaseUntil <= clock.GetUtcNow().AddSeconds(10)) { continue; }
            if (intent.Attempts > 1) { metrics.Retry(intent.LastFailureCode); }
            if (!EnvelopeCodec.TryDecode(intent.ProtectedEnvelope!, "notification.email.requested", 1, "identity", out var message, out _)
                || message!.MessageId != intent.ExternalId || message.Type != intent.Type || message.Version != intent.Version
                || message.ExpiresAt != intent.ExpiresAt || message.OccurredAt != intent.OccurredAt || message.SupersessionKey != intent.SupersessionKey
                || message.Traceparent != intent.Traceparent || message.Tracestate != intent.Tracestate)
            {
                await store.FailAsync(intent.Id, lease, clock.GetUtcNow(), cancellationToken);
                metrics.Failure("invalid-contract");
                activity?.SetStatus(ActivityStatusCode.Error);
                LogPublishFailed(logger, intent.ExternalId, "invalid-contract");
                continue;
            }
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await publisher.PublishAsync(intent, timeout.Token);
                // Crash/erro antes deste commit permite reentrega com o mesmo id após vencer a lease.
                await store.ConfirmAsync(intent.Id, lease, clock.GetUtcNow(), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                var suspended = IsConfigurationFailure(exception);
                var code = suspended ? "configuration-suspended" : exception is PublishException ? "broker-rejected" : "publish-unconfirmed";
                metrics.Failure(code);
                activity?.SetStatus(ActivityStatusCode.Error);
                LogPublishFailed(logger, intent.ExternalId, code, exception);
                try { await publisher.DisposeAsync(); }
                catch (Exception cleanupException) { LogCycleFailed(logger, cleanupException); }
                var now = clock.GetUtcNow();
                var seconds = Math.Min(300, Math.Pow(2, Math.Min(intent.Attempts, 9)) * (0.8 + Random.Shared.NextDouble() * 0.4));
                await store.RetryAsync(intent.Id, lease, now, suspended ? null : now.AddSeconds(seconds), code, cancellationToken);
            }
        }
        var snapshot = await store.SnapshotAsync(clock.GetUtcNow(), cancellationToken);
        metrics.Snapshot(snapshot.Count, snapshot.OldestAgeSeconds);
        metrics.TerminalSnapshot(await store.TerminalSnapshotAsync(cancellationToken));
    }

    private static bool IsConfigurationFailure(Exception exception)
    {
        var pending = new Stack<Exception>();
        pending.Push(exception);
        for (var visited = 0; visited < 32 && pending.Count > 0; visited++)
        {
            var current = pending.Pop();
            if (current is PublishException { IsReturn: true } or AuthenticationFailureException or System.Security.Authentication.AuthenticationException
                || current is OperationInterruptedException { ShutdownReason.ReplyCode: 403 or 404 or 405 or 406 or 530 }) { return true; }
            if (current is AggregateException aggregate)
            { foreach (var inner in aggregate.InnerExceptions.Take(16)) { pending.Push(inner); } }
            else if (current.InnerException is not null) { pending.Push(current.InnerException); }
        }
        return false;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Publicação de {MessageId} não confirmada: {Code}; intenção recuperável.")]
    private static partial void LogPublishFailed(ILogger logger, Guid messageId, string code, Exception? exception = null);
    [LoggerMessage(Level = LogLevel.Error, Message = "Ciclo do publicador interrompido; progresso persistido será retomado.")]
    private static partial void LogCycleFailed(ILogger logger, Exception exception);
}
