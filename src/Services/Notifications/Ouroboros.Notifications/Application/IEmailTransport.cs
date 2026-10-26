using Ouroboros.Notifications.Contracts;

namespace Ouroboros.Notifications.Application;

internal interface IEmailTransport
{
    Task SendAsync(Guid messageId, EmailContent content, CancellationToken cancellationToken);
}
