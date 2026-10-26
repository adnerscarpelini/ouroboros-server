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
            return source.StartActivity(name, kind, context, links: [Link(context)]);
        }
        return source.StartActivity(name, kind, new ActivityContext());
    }

    public static Activity? Start(ActivitySource source, string name, ActivityKind kind, MessageEnvelope? message) =>
        Start(source, name, kind, message?.Traceparent, message?.Tracestate);

    public static Activity? StartBatch(ActivitySource source, IEnumerable<(string? Traceparent, string? Tracestate)> messages)
    {
        var links = messages.Select(message => ActivityContext.TryParse(message.Traceparent, message.Tracestate, true, out var context)
            ? new ActivityLink?(Link(context)) : null).Where(link => link.HasValue).Select(link => link!.Value).ToArray();
        // parentId vazio cria uma raiz mesmo que o ciclo seja disparado dentro de uma requisição.
        return source.StartActivity("outbox.publish.cycle", ActivityKind.Internal, parentId: "", links: links);
    }

    public static void Describe(Activity? activity, string operation, Guid? messageId)
    {
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.destination.name", "ouroboros.notifications");
        activity?.SetTag("messaging.rabbitmq.destination.routing_key", "email.requested.v1");
        activity?.SetTag("messaging.operation.name", operation);
        activity?.SetTag("messaging.operation.type", operation == "publish" ? "send" : "process");
        if (messageId.HasValue) { activity?.SetTag("messaging.message.id", messageId.Value.ToString("D")); }
    }

    // Links exportados só precisam dos identificadores; tracestate é entrada externa, sem conteúdo diagnóstico.
    private static ActivityLink Link(ActivityContext context) => new(new ActivityContext(context.TraceId, context.SpanId, context.TraceFlags, isRemote: true));
}
