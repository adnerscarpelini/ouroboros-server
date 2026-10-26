using Ouroboros.BuildingBlocks.Domain;

namespace Ouroboros.Identity.Domain.Notifications;

internal enum EmailIntentState { Pending, Published, Cancelled, Expired, Failed }

internal sealed class EmailIntent : Entity
{
    private EmailIntent(DateTimeOffset occurredAt, DateTimeOffset expiresAt, string? supersessionKey, string? traceparent, string? tracestate)
    {
        if (expiresAt <= occurredAt) { throw new ArgumentException("Prazo da intenção inválido."); }
        OccurredAt = occurredAt;
        ExpiresAt = expiresAt;
        SupersessionKey = supersessionKey;
        Traceparent = traceparent;
        Tracestate = tracestate;
    }

    public string Type { get; private set; } = "notification.email.requested";
    public int Version { get; private set; } = 1;
    public DateTimeOffset OccurredAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public string? SupersessionKey { get; private set; }
    public string? Traceparent { get; private set; }
    public string? Tracestate { get; private set; }
    public byte[]? ProtectedEnvelope { get; private set; }
    public string? ProtectionKeyId { get; private set; }
    public EmailIntentState State { get; private set; }
    public int Attempts { get; private set; }
    public DateTimeOffset? PublishedAt { get; private set; }
    public Guid SourceTokenId { get; private set; }

    public static EmailIntent Create(DateTimeOffset now, DateTimeOffset expiresAt, string supersessionKey, string? traceparent, string? tracestate) =>
        new(now, expiresAt, supersessionKey, traceparent, tracestate);

    public void Protect(byte[] envelope, string keyId)
    {
        if (ProtectedEnvelope is not null || envelope.Length is 0 or > 32_768 || string.IsNullOrWhiteSpace(keyId))
        { throw new ArgumentException("Proteção da intenção inválida."); }
        ProtectedEnvelope = envelope;
        ProtectionKeyId = keyId;
    }

    public void BindToToken(Guid tokenId)
    {
        if (tokenId == Guid.Empty || SourceTokenId != Guid.Empty) { throw new ArgumentException("Associação da intenção inválida."); }
        SourceTokenId = tokenId;
    }
}
