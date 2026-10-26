using OpenTelemetry;
using OpenTelemetry.Logs;

namespace Ouroboros.Observability;

// Primeiro processador do pipeline de logs: atribui a identidade da ocorrência e sanitiza o registro antes que
// qualquer exportador (console ou OTLP) o receba. Roda no thread que chamou o ILogger, sem E/S.
internal sealed class SanitizingLogProcessor : BaseProcessor<LogRecord>
{
    internal const string OccurrenceIdAttribute = "log.record.uid";
    internal const string UntemplatedMessage = "(mensagem sem template omitida)";

    public override void OnEnd(LogRecord data)
    {
        try
        {
            var attributes = new List<KeyValuePair<string, object?>>(Math.Min(data.Attributes?.Count ?? 0, DiagnosticRedaction.MaximumAttributes) + 4)
            {
                new(OccurrenceIdAttribute, Guid.CreateVersion7().ToString("D")),
            };
            foreach (var (key, value) in (data.Attributes ?? []).Take(DiagnosticRedaction.MaximumAttributes))
            {
                // O template já está no Body; a identidade da ocorrência nunca vem de quem chamou o ILogger.
                if (key is OccurrenceIdAttribute or "{OriginalFormat}") { continue; }
                attributes.Add(new(DiagnosticRedaction.Sanitize(key, 128), DiagnosticRedaction.SanitizeValue(key, value)));
            }
            if (data.Exception is { } exception)
            {
                var (type, message, stack) = DiagnosticRedaction.Describe(exception);
                attributes.Add(new("exception.type", type));
                attributes.Add(new("exception.message", message));
                attributes.Add(new("exception.stacktrace", stack));
                // O exportador nunca serializa a exceção original (mensagens internas, Data, ToString).
                data.Exception = null;
            }
            data.Attributes = attributes;
            // A mensagem é recomposta do template com os valores já sanitizados: o formatter do ILogger não é chamado,
            // porque usaria ToString de objetos e os valores de chaves sensíveis. Sem template, o SDK entrega no Body o
            // texto já formatado, que passa só pelos padrões; sem Body, não há texto.
            data.FormattedMessage = DiagnosticRedaction.Sanitize(
                data.Body is { } template ? MessageTemplate.Render(template, attributes) : UntemplatedMessage, DiagnosticRedaction.MaximumMessageLength);
            data.Body = DiagnosticRedaction.Sanitize(data.Body, DiagnosticRedaction.MaximumMessageLength);
        }
        catch (Exception)
        {
            // Sanitização falhou: descarta o conteúdo, preserva a ocorrência e nunca propaga ao chamador.
            data.Exception = null;
            data.Attributes = [new(OccurrenceIdAttribute, Guid.CreateVersion7().ToString("D")), new("ouroboros.diagnostic.discarded", true)];
            data.FormattedMessage = "Diagnóstico descartado: sanitização indisponível.";
            data.Body = data.FormattedMessage;
        }
    }
}
