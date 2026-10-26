using Microsoft.Extensions.Hosting;

namespace Ouroboros.Identity.Infrastructure.Security;

internal sealed class SigningKeysStartupCheck(TokenKeyRing keys) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = keys.SigningCredentials();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
