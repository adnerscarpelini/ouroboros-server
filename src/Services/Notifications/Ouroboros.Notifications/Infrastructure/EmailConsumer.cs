using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ouroboros.Messaging;
using Ouroboros.Notifications.Infrastructure.Persistence;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Ouroboros.Notifications.Infrastructure;

// Aceitação local durável antecede o ACK; envio ao provedor ocorre em outro executor.
internal sealed partial class EmailConsumer(IOptions<NotificationOptions> options, IHostEnvironment environment,
    IServiceScopeFactory scopes, ILogger<EmailConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Consumer.Enabled) { LogDisabled(logger); return; }
        var settings = options.Value.Broker;
        var factory = RabbitConnectionFactory.Create(settings.Host, settings.Port, settings.User, settings.Password, environment.IsDevelopment());
        // A reconexão completa reestabelece a assinatura sem dar configure ao runtime.
        factory.AutomaticRecoveryEnabled = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConsumeAsync(factory, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) when (exception is RabbitMQ.Client.Exceptions.RabbitMQClientException or IOException or TimeoutException)
            {
                LogBrokerUnavailable(logger);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task ConsumeAsync(ConnectionFactory factory, CancellationToken stoppingToken)
    {
        await using var connection = await factory.CreateConnectionAsync(stoppingToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
        await channel.QueueDeclarePassiveAsync("notifications.email.v1", stoppingToken);
        await channel.BasicQosAsync(0, 1, false, stoppingToken);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.ConnectionShutdownAsync += (_, _) => { ended.TrySetException(new IOException("broker-unavailable")); return Task.CompletedTask; };
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            Guid? id = null;
            // O envelope válido traz o contexto W3C da origem; inválido, a aceitação ocorre em um trace novo.
            EnvelopeCodec.TryDecode(delivery.Body, "notification.email.requested", 1, "identity", out var envelope, out string _);
            using var activity = MessageTracing.Start(NotificationsTelemetry.Source, "inbox.accept", ActivityKind.Consumer, envelope);
            try
            {
                id = Guid.TryParse(delivery.BasicProperties.MessageId, out var parsed) ? parsed : null;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                await using var scope = scopes.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<InboxAcceptance>()
                    .AcceptAsync(delivery.Body, delivery.BasicProperties.MessageId, timeout.Token);
                await channel.BasicAckAsync(delivery.DeliveryTag, false, stoppingToken);
                LogHandled(logger, id, result.ToString());
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception)
            {
                LogSuspended(logger, id, "acceptance-unavailable");
                ended.TrySetException(new IOException("acceptance-unavailable"));
            }
        };
        await channel.BasicConsumeAsync("notifications.email.v1", false, consumer, stoppingToken);
        await ended.Task.WaitAsync(stoppingToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Consumo de Notifications desabilitado.")]
    private static partial void LogDisabled(ILogger logger);
    [LoggerMessage(Level = LogLevel.Warning, Message = "Broker de Notifications indisponível; nova conexão será tentada.")]
    private static partial void LogBrokerUnavailable(ILogger logger);
    [LoggerMessage(Level = LogLevel.Error, Message = "Notifications suspenso para {MessageId}: {Code}. Nenhum ACK.")]
    private static partial void LogSuspended(ILogger logger, Guid? messageId, string code);
    [LoggerMessage(Level = LogLevel.Information, Message = "Notifications processou {MessageId}: {State}.")]
    private static partial void LogHandled(ILogger logger, Guid? messageId, string state);
}
