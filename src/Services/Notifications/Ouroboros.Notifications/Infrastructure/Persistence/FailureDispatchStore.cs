using Microsoft.EntityFrameworkCore;
using Ouroboros.Notifications.Domain;

namespace Ouroboros.Notifications.Infrastructure.Persistence;

internal sealed class FailureDispatchStore(NotificationsDbContext context)
{
    internal Task<List<FailureDispatch>> ClaimAsync(Guid lease, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var until = now.AddMinutes(2);
        return context.Failures.FromSqlInterpolated($"""
            UPDATE notifications.failure_outbox SET lease_id={lease}, lease_until={until}, attempts=attempts+1
            WHERE id IN (
                SELECT id FROM notifications.failure_outbox WHERE published_at IS NULL AND NOT suspended AND retain_until>{now}
                  AND (next_attempt_at IS NULL OR next_attempt_at<={now}) AND (lease_until IS NULL OR lease_until<={now})
                ORDER BY occurred_at FOR UPDATE SKIP LOCKED LIMIT 4)
            RETURNING *
            """).AsNoTracking().ToListAsync(cancellationToken);
    }

    internal Task<bool> IsOwnedAsync(Guid id, Guid lease, DateTimeOffset now, CancellationToken cancellationToken) =>
        context.Failures.AnyAsync(row => row.Id == id && row.LeaseId == lease && row.LeaseUntil > now
            && row.PublishedAt == null && row.RetainUntil > now && !row.Suspended, cancellationToken);

    internal Task<int> ConfirmAsync(Guid id, Guid lease, DateTimeOffset now, CancellationToken cancellationToken) =>
        context.Failures.Where(row => row.Id == id && row.LeaseId == lease && row.PublishedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.PublishedAt, now).SetProperty(row => row.LeaseId, (Guid?)null)
                .SetProperty(row => row.LeaseUntil, (DateTimeOffset?)null).SetProperty(row => row.NextAttemptAt, (DateTimeOffset?)null), cancellationToken);

    internal Task<int> RetryAsync(Guid id, Guid lease, bool suspended, DateTimeOffset? next, CancellationToken cancellationToken) =>
        context.Failures.Where(row => row.Id == id && row.LeaseId == lease && row.PublishedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.LeaseId, (Guid?)null)
                .SetProperty(row => row.LeaseUntil, (DateTimeOffset?)null).SetProperty(row => row.Suspended, suspended)
                .SetProperty(row => row.NextAttemptAt, next), cancellationToken);
}
