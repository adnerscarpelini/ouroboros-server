using Microsoft.EntityFrameworkCore;
using Ouroboros.Identity.Domain.Notifications;

namespace Ouroboros.Identity.Infrastructure.Persistence;

internal sealed class EmailOutboxStore(IdentityDbContext context)
{
    internal Task<List<EmailIntent>> ClaimAsync(Guid leaseId, DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
    {
        if (batchSize is < 1 or > 8) { throw new ArgumentOutOfRangeException(nameof(batchSize)); }
        // Uma única instrução/autocommit: as travas terminam antes de abrir a conexão com o broker.
        var until = now.AddMinutes(5);
        return context.EmailIntents.FromSqlInterpolated($"""
            UPDATE identity.email_intents SET lease_id={leaseId}, lease_until={until}, attempts=attempts+1, updated_at={now}
            WHERE id IN (
                SELECT id FROM identity.email_intents
                WHERE state='Pending' AND protected_envelope IS NOT NULL AND expires_at>{now}
                  AND last_failure_code IS DISTINCT FROM 'configuration-suspended'
                  AND (next_attempt_at IS NULL OR next_attempt_at<={now})
                  AND (lease_until IS NULL OR lease_until<={now})
                ORDER BY id FOR UPDATE SKIP LOCKED LIMIT {batchSize})
            RETURNING *
            """).AsNoTracking().ToListAsync(cancellationToken);
    }

    internal Task<bool> IsOwnedAsync(long id, Guid leaseId, DateTimeOffset now, CancellationToken cancellationToken) =>
        context.EmailIntents.AnyAsync(intent => intent.Id == id && intent.LeaseId == leaseId && intent.LeaseUntil > now
            && intent.State == EmailIntentState.Pending && intent.ExpiresAt > now && intent.ProtectedEnvelope != null, cancellationToken);

    internal Task<int> ConfirmAsync(long id, Guid leaseId, DateTimeOffset now, CancellationToken cancellationToken) =>
        CompleteAsync(id, leaseId, now, EmailIntentState.Published, null, cancellationToken);

    internal Task<int> FailAsync(long id, Guid leaseId, DateTimeOffset now, CancellationToken cancellationToken) =>
        CompleteAsync(id, leaseId, now, EmailIntentState.Failed, "invalid-contract", cancellationToken);

    private Task<int> CompleteAsync(long id, Guid leaseId, DateTimeOffset now, EmailIntentState state, string? code, CancellationToken cancellationToken) =>
        context.EmailIntents.Where(intent => intent.Id == id && intent.LeaseId == leaseId && intent.State == EmailIntentState.Pending)
            .ExecuteUpdateAsync(update => update.SetProperty(intent => intent.State, state)
                .SetProperty(intent => intent.PublishedAt, state == EmailIntentState.Published ? now : (DateTimeOffset?)null).SetProperty(intent => intent.UpdatedAt, now)
                .SetProperty(intent => intent.ProtectedEnvelope, (byte[]?)null).SetProperty(intent => intent.ProtectionKeyId, (string?)null)
                .SetProperty(intent => intent.SupersessionKey, (string?)null).SetProperty(intent => intent.Traceparent, (string?)null)
                .SetProperty(intent => intent.Tracestate, (string?)null).SetProperty(intent => intent.LeaseId, (Guid?)null)
                .SetProperty(intent => intent.LeaseUntil, (DateTimeOffset?)null).SetProperty(intent => intent.NextAttemptAt, (DateTimeOffset?)null)
                .SetProperty(intent => intent.LastFailureCode, code), cancellationToken);

    internal Task<int> RetryAsync(long id, Guid leaseId, DateTimeOffset now, DateTimeOffset? next, string code, CancellationToken cancellationToken) =>
        context.EmailIntents.Where(intent => intent.Id == id && intent.LeaseId == leaseId && intent.State == EmailIntentState.Pending)
            .ExecuteUpdateAsync(update => update.SetProperty(intent => intent.LeaseId, (Guid?)null)
                .SetProperty(intent => intent.LeaseUntil, (DateTimeOffset?)null).SetProperty(intent => intent.NextAttemptAt, next)
                .SetProperty(intent => intent.LastFailureCode, code).SetProperty(intent => intent.UpdatedAt, now), cancellationToken);

    internal async Task<(long Count, double OldestAgeSeconds)> SnapshotAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var pending = context.EmailIntents.Where(intent => intent.State == EmailIntentState.Pending && intent.ExpiresAt > now);
        var count = await pending.LongCountAsync(cancellationToken);
        var oldest = await pending.MinAsync(intent => (DateTimeOffset?)intent.OccurredAt, cancellationToken);
        return (count, oldest is null ? 0 : Math.Max(0, (now - oldest.Value).TotalSeconds));
    }
}
