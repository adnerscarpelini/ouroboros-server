using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Globalization;

namespace Ouroboros.Observability;

// Sinaliza falhas e descartes do próprio pipeline (eventos do SDK OpenTelemetry) por uma saída independente
// (stderr), nunca pelo ILogger: a falha do destino não volta ao pipeline nem inicia uma cascata. Cada evento é
// escrito no máximo uma vez por intervalo; as repetições suprimidas são contadas na linha seguinte.
internal sealed class TelemetryFailureSignal : EventListener
{
    private static readonly Lazy<TelemetryFailureSignal> s_process = new(() => new TelemetryFailureSignal(Console.Error, TimeSpan.FromSeconds(30)));
    private readonly ConcurrentDictionary<(string Source, int Event), (DateTimeOffset Last, int Suppressed)> _seen = new();
    private readonly TextWriter? _output;
    private readonly TimeSpan _interval;

    internal TelemetryFailureSignal(TextWriter output, TimeSpan interval)
    {
        _output = output;
        _interval = interval;
        // Fontes criadas antes do construtor terminar são habilitadas aqui; as posteriores, em OnEventSourceCreated.
        foreach (var source in EventSource.GetSources()) { Enable(source); }
    }

    internal static void EnsureStarted() => _ = s_process.Value;

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        // Chamado também pelo construtor base, antes dos campos: _output nulo indica essa fase.
        if (_output is not null) { Enable(eventSource); }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        var output = _output;
        if (output is null || eventData.Level > EventLevel.Warning) { return; }
        var key = (eventData.EventSource.Name, eventData.EventId);
        var now = DateTimeOffset.UtcNow;
        var suppressed = 0;
        var write = false;
        _seen.AddOrUpdate(key, _ => { write = true; return (now, 0); }, (_, previous) =>
        {
            if (now - previous.Last < _interval) { write = false; return (previous.Last, previous.Suppressed + 1); }
            write = true;
            suppressed = previous.Suppressed;
            return (now, 0);
        });
        if (!write) { return; }
        try
        {
            var detail = eventData.Message is null ? "" : string.Format(CultureInfo.InvariantCulture, eventData.Message, eventData.Payload?.ToArray() ?? []);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"ouroboros-observability {now:O} {eventData.Level} {eventData.EventSource.Name}#{eventData.EventId}: {DiagnosticRedaction.Sanitize(detail.ReplaceLineEndings(" "), 512)} (suprimidos desde o último aviso: {suppressed})"));
        }
        catch (Exception exception) when (exception is FormatException or IOException or ObjectDisposedException)
        {
            // Sem saída independente disponível, o sinal é perdido; nunca afeta o negócio.
        }
    }

    private void Enable(EventSource source)
    {
        // SDK (descartes, falhas de processadores) e exportadores; avisos de composição do Hosting não são falhas.
        if (source.Name is "OpenTelemetry-Sdk" || source.Name.StartsWith("OpenTelemetry-Exporter-", StringComparison.Ordinal))
        { EnableEvents(source, EventLevel.Warning); }
    }
}
