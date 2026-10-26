using System.Diagnostics;
using Ouroboros.Messaging.Contracts;

namespace Ouroboros.Messaging;

// Continua, no processamento de uma mensagem, o contexto W3C gravado no envelope pela origem. O contexto só
// correlaciona diagnósticos: identidade, deduplicação e autorização usam o MessageId e as credenciais.
public static class MessageTracing
{
    // Contexto ausente ou inválido não é reaproveitado: a atividade não herda nada da mensagem e, nos laços de
    // processamento (sem atividade corrente), começa um trace novo e válido.
    public static Activity? Start(ActivitySource source, string name, ActivityKind kind, string? traceparent, string? tracestate)
    {
        if (traceparent is { Length: <= 55 } && tracestate is null or { Length: <= 512 }
            && ActivityContext.TryParse(traceparent, tracestate, isRemote: true, out var context))
        {
            return source.StartActivity(name, kind, context);
        }
        return source.StartActivity(name, kind, new ActivityContext());
    }

    public static Activity? Start(ActivitySource source, string name, ActivityKind kind, MessageEnvelope? message) =>
        Start(source, name, kind, message?.Traceparent, message?.Tracestate);
}
