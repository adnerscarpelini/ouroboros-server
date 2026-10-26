using System.Text.Json.Serialization;
using Ouroboros.Messaging.Contracts;

namespace Ouroboros.Notifications.Contracts;

public interface INotificationDelivery
{
    Task<NotificationDeliveryResult> ExecuteAsync(MessageEnvelope message, CancellationToken cancellationToken);
}

public enum NotificationDeliveryResult { Delivered, Expired }

// DTO próprio de Notifications; não depende do assembly do produtor.
public sealed record EmailContent(
    [property: JsonRequired] string Recipient,
    [property: JsonRequired] string Subject,
    [property: JsonRequired] string Text,
    [property: JsonRequired] string Purpose);
