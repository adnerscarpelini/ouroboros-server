using System.Diagnostics;
using System.Text.Json;
using Ouroboros.Messaging.Contracts;

namespace Ouroboros.Messaging;

public static class EnvelopeCodec
{
    public const int MaximumEnvelopeBytes = 32_768;
    public const int MaximumPayloadBytes = 16_384;
    public const string ProtectionAlgorithm = "RSA-OAEP-256+A256GCM";
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false };

    // Metadados canônicos v1 autenticados por AES-GCM, sem incluir o próprio ciphertext/tag.
    public static byte[] AuthenticationData(MessageEnvelope message) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        message.MessageId,
        message.Type,
        message.Version,
        message.OccurredAt,
        message.ExpiresAt,
        message.Producer,
        message.CorrelationId,
        message.CausationId,
        message.SupersessionKey,
        message.Traceparent,
        message.Tracestate,
        message.Payload.Algorithm,
        message.Payload.KeyId,
    }, s_json);

    public static byte[] EncodeFailure(MessageFailureReference reference, DateTimeOffset originalExpiresAt)
    {
        if (reference.FailureId == Guid.Empty || reference.MessageId == Guid.Empty
            || reference.Code is not ("invalid-contract" or "unsupported-version" or "content-conflict"
                or "provider-permanent" or "retry-exhausted" or "expired" or "protection-unavailable")
            || reference.OccurredAt.Offset != TimeSpan.Zero || reference.RetainUntil.Offset != TimeSpan.Zero
            || reference.RetainUntil <= reference.OccurredAt || reference.RetainUntil > originalExpiresAt.AddDays(30))
        {
            throw new ArgumentException("Referência de falha inválida.");
        }
        return JsonSerializer.SerializeToUtf8Bytes(reference, s_json);
    }

    public static byte[] Encode(MessageEnvelope message)
    {
        if (!IsStructurallyValid(message)) { throw new ArgumentException("Envelope de mensagem inválido."); }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, s_json);
        if (bytes.Length > MaximumEnvelopeBytes) { throw new ArgumentException("Envelope excede o limite."); }
        return bytes;
    }

    // O contrato esperado é explícito em cada adaptador; versão desconhecida nunca vira v1 por inferência.
    public static bool TryDecode(ReadOnlyMemory<byte> bytes, string expectedType, int expectedVersion, string expectedProducer,
        out MessageEnvelope? message, out string errorCode)
    {
        message = null;
        errorCode = "invalid-contract";
        if (bytes.Length is 0 or > MaximumEnvelopeBytes) { return false; }
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (!HasUniqueFields(document.RootElement)) { return false; }
            var candidate = document.RootElement.Deserialize<MessageEnvelope>(s_json);
            if (candidate is null || !IsStructurallyValid(candidate)) { return false; }
            if (candidate.Type != expectedType || candidate.Version != expectedVersion)
            {
                errorCode = "unsupported-version";
                return false;
            }
            if (candidate.Producer != expectedProducer) { return false; }
            message = candidate;
            errorCode = "";
            return true;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    private static bool HasUniqueFields(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return value.EnumerateObject().All(property => names.Add(property.Name) && HasUniqueFields(property.Value));
        }
        return value.ValueKind != JsonValueKind.Array || value.EnumerateArray().All(HasUniqueFields);
    }

    private static bool IsStructurallyValid(MessageEnvelope message)
    {
        var payload = message.Payload;
        return message.MessageId != Guid.Empty && message.CorrelationId != Guid.Empty && message.CausationId != Guid.Empty
            && IsIdentifier(message.Type, 128) && message.Version > 0 && IsIdentifier(message.Producer, 64)
            && IsIdentifier(message.SupersessionKey, 128)
            && message.OccurredAt.Offset == TimeSpan.Zero && message.ExpiresAt.Offset == TimeSpan.Zero
            && message.ExpiresAt > message.OccurredAt
            && (message.Traceparent is null ? message.Tracestate is null : message.Traceparent.Length <= 55
                && (message.Tracestate is null || message.Tracestate.Length <= 512)
                && ActivityContext.TryParse(message.Traceparent, message.Tracestate, out _))
            && payload is not null && payload.Algorithm == ProtectionAlgorithm && IsIdentifier(payload.KeyId, 128)
            && IsBase64(payload.WrappedKey, 256, 1024) && IsBase64(payload.Nonce, 12, 12)
            && IsBase64(payload.Tag, 16, 16) && IsBase64(payload.Ciphertext, 1, MaximumPayloadBytes);
    }

    private static bool IsIdentifier(string? value, int maximum) => value is { Length: > 0 } && value.Length <= maximum
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or ':' or '_' or '-');

    private static bool IsBase64(string? value, int minimumBytes, int maximumBytes)
    {
        if (value is null || value.Length > ((maximumBytes + 2) / 3) * 4) { return false; }
        Span<byte> bytes = stackalloc byte[maximumBytes];
        return Convert.TryFromBase64String(value, bytes, out var length) && length >= minimumBytes && length <= maximumBytes
            && Convert.ToBase64String(bytes[..length]) == value;
    }
}
