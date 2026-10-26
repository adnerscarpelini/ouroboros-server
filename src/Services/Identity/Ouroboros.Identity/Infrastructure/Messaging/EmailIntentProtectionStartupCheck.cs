using Microsoft.Extensions.Hosting;
using Ouroboros.Identity.Application.Notifications;

namespace Ouroboros.Identity.Infrastructure.Messaging;

internal sealed class EmailIntentProtectionStartupCheck(ProtectedEmailIntentFactory factory, TimeProvider clock) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Prova local de configuração, sem persistência, token de negócio ou rede.
        _ = factory.Create(new EmailMessage("configuration-probe@invalid.local", "configuration-probe", "configuration-probe"),
            "registration", clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(1));
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
