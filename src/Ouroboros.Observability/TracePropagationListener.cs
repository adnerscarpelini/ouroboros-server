using System.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace Ouroboros.Observability;

// Sem um ouvinte, ActivitySource.StartActivity devolve null e o processamento de mensagens perderia o contexto
// nos logs. Este ouvinte só pede propagação (sem gravar dados do span) para as fontes do Ouroboros; um ouvinte
// que peça mais, como um SDK de tracing, prevalece.
internal sealed class TracePropagationListener : IHostedService, IDisposable
{
    private readonly ActivityListener _listener = new()
    {
        ShouldListenTo = source => source.Name.StartsWith("Ouroboros.", StringComparison.Ordinal),
        Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.PropagationData,
    };

    public Task StartAsync(CancellationToken cancellationToken)
    {
        ActivitySource.AddActivityListener(_listener);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => _listener.Dispose();
}
