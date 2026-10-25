using System.Text.Json.Serialization;

namespace Ouroboros.Messaging.Contracts;

// Envelope técnico imutável. O conteúdo de negócio é opaco para a biblioteca de transporte.
public sealed record MessageEnvelope(
    [property: JsonRequired] Guid MessageId,
    [property: JsonRequired] string Type,
    [property: JsonRequired] int Version,
    [property: JsonRequired] DateTimeOffset OccurredAt,
    [property: JsonRequired] DateTimeOffset ExpiresAt,
    [property: JsonRequired] string Producer,
    [property: JsonRequired] Guid CorrelationId,
    [property: JsonRequired] Guid CausationId,
    [property: JsonRequired] string SupersessionKey,
    [property: JsonRequired] string? Traceparent,
    [property: JsonRequired] string? Tracestate,
    [property: JsonRequired] ProtectedMessagePayload Payload);

public sealed record ProtectedMessagePayload(
    [property: JsonRequired] string Algorithm,
    [property: JsonRequired] string KeyId,
    [property: JsonRequired] string WrappedKey,
    [property: JsonRequired] string Nonce,
    [property: JsonRequired] string Tag,
    [property: JsonRequired] string Ciphertext);

// A fila de falhas nunca recebe o envelope original, nem mesmo seu ciphertext.
public sealed record MessageFailureReference(Guid FailureId, Guid? MessageId, string Code, DateTimeOffset OccurredAt, DateTimeOffset RetainUntil)
{
    public string Type => "notification.email.failed";
    public int Version => 1;
}
