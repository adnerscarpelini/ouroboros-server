using Ouroboros.Messaging.Contracts;
using Ouroboros.Notifications.Contracts;

namespace Ouroboros.Notifications.Application;

// Entrega direta de desenvolvimento. A aceitação durável e o executor persistente são a #87.
internal sealed class NotificationDelivery(IEmailPayloadReader payloads, IEmailTransport transport, TimeProvider clock) : INotificationDelivery
{
    public async Task<NotificationDeliveryResult> ExecuteAsync(MessageEnvelope message, CancellationToken cancellationToken)
    {
        if (message.Type != "notification.email.requested" || message.Version != 1 || message.Producer != "identity")
        { throw new InvalidOperationException("Contrato de e-mail incompatível."); }
        if (message.ExpiresAt <= clock.GetUtcNow()) { return NotificationDeliveryResult.Expired; }
        var content = payloads.Read(message);
        if (message.ExpiresAt <= clock.GetUtcNow()) { return NotificationDeliveryResult.Expired; }
        await transport.SendAsync(message.MessageId, content, cancellationToken);
        return NotificationDeliveryResult.Delivered;
    }
}
