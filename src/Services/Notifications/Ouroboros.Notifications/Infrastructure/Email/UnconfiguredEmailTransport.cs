using Ouroboros.Notifications.Application;
using Ouroboros.Notifications.Contracts;

namespace Ouroboros.Notifications.Infrastructure.Email;

internal sealed class UnconfiguredEmailTransport : IEmailTransport
{
    public Task SendAsync(Guid messageId, EmailContent content, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Nenhum provedor configurado em Notifications:Email:Provider.");
}
