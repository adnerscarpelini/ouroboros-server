namespace Ouroboros.Notifications.Domain;

internal enum InboxState { Accepted, Sending, Delivered, Failed, Expired, Cancelled }

internal sealed class InboxMessage
{
    public Guid MessageId { get; set; }
    public byte[] Fingerprint { get; set; } = [];
    public string FingerprintKeyId { get; set; } = "";
    public byte[]? ProtectedEnvelope { get; set; }
    public string? SupersessionId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset AcceptedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public InboxState State { get; set; }
    public int Attempts { get; set; }
    public bool Suspended { get; set; }
    public string? LastFailureCode { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
}

internal sealed class SupersessionCursor
{
    public string Id { get; set; } = "";
    public DateTimeOffset LastOccurredAt { get; set; }
    public Guid LastMessageId { get; set; }
    public DateTimeOffset RetainUntil { get; set; }
}

internal sealed class FailureDispatch
{
    public Guid Id { get; set; }
    public Guid? MessageId { get; set; }
    public string Code { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset OriginalExpiresAt { get; set; }
    public DateTimeOffset RetainUntil { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public int Attempts { get; set; }
    public bool Suspended { get; set; }
}

internal sealed class ProcessingRecovery
{
    public Guid Id { get; set; }
    public Guid MessageId { get; set; }
    public string Actor { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset RetainUntil { get; set; }
}
