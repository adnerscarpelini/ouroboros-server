namespace Ouroboros.Notifications.Application;

internal enum DeliveryFailure { Transient, Permanent, Configuration, InvalidContent }

internal sealed class NotificationDeliveryException(DeliveryFailure kind, TimeSpan? retryAfter = null)
    : InvalidOperationException("Entrega não concluída; classificação sanitizada disponível.")
{
    internal DeliveryFailure Kind { get; } = kind;
    internal TimeSpan? RetryAfter { get; } = retryAfter;
}
