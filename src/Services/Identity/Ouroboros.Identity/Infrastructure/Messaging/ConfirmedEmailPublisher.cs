using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Ouroboros.Identity.Domain.Notifications;
using Ouroboros.Messaging;
using RabbitMQ.Client;

namespace Ouroboros.Identity.Infrastructure.Messaging;

internal sealed class ConfirmedEmailPublisher(IOptions<EmailPublisherOptions> options, IHostEnvironment environment) : IAsyncDisposable
{
    private IConnection? _connection;
    private IChannel? _channel;

    internal async Task PublishAsync(EmailIntent intent, CancellationToken cancellationToken)
    {
        if (_channel is null || !_channel.IsOpen)
        {
            await DisposeAsync();
            var settings = options.Value;
            var factory = RabbitConnectionFactory.Create(settings.Host, settings.Port, settings.User, settings.Password, environment.IsDevelopment());
            factory.AutomaticRecoveryEnabled = false;
            _connection = await factory.CreateConnectionAsync(cancellationToken);
            _channel = await _connection.CreateChannelAsync(new CreateChannelOptions(true, true), cancellationToken);
        }
        // Tracking converte basic.return/mandatory e basic.nack em falha do await, além de aguardar o confirm.
        var properties = new BasicProperties
        {
            Persistent = true,
            ContentType = "application/json",
            Type = intent.Type,
            MessageId = intent.ExternalId.ToString("D"),
            AppId = "identity",
        };
        await _channel.BasicPublishAsync("ouroboros.notifications", "email.requested.v1", true, properties,
            intent.ProtectedEnvelope!, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        var channel = _channel;
        var connection = _connection;
        _channel = null;
        _connection = null;
        try { if (channel is not null) { await channel.DisposeAsync(); } }
        finally { if (connection is not null) { await connection.DisposeAsync(); } }
    }
}
