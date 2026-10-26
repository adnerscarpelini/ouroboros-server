using System.Diagnostics;
using OpenTelemetry;

namespace Ouroboros.Observability;

// Executa antes do processador em lote. Eventos, links e baggage não levam payload aos destinos.
internal sealed class SanitizingSpanProcessor : BaseProcessor<Activity>
{
    public override void OnEnd(Activity activity)
    {
        activity.DisplayName = DiagnosticRedaction.Sanitize(activity.DisplayName, 256);
        activity.TraceStateString = null;
        activity.SetStatus(activity.Status);
        foreach (var tag in activity.TagObjects.ToArray())
        {
            var forbidden = DiagnosticRedaction.IsSensitiveKey(tag.Key)
                || tag.Key is "url.full" or "url.query" or "url.path" or "http.target" or "user.id" or "enduser.id"
                || tag.Key.StartsWith("http.request.header.", StringComparison.Ordinal)
                || tag.Key.StartsWith("http.response.header.", StringComparison.Ordinal);
            var value = forbidden ? null : tag.Value switch
            {
                string text => DiagnosticRedaction.Sanitize(text, DiagnosticRedaction.MaximumValueLength),
                bool or int or long or double or float => tag.Value,
                _ => null,
            };
            activity.SetTag(tag.Key, value);
        }
        // Só são instrumentados HTTP e as fontes próprias, que não criam eventos nem atributos de links.
        // Não habilitar RecordException ou fontes automáticas do RabbitMQ sem rever esta fronteira.
    }
}
