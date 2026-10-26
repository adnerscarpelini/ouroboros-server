using Microsoft.EntityFrameworkCore;
using Ouroboros.Notifications.Domain;

namespace Ouroboros.Notifications.Infrastructure.Persistence;

internal sealed class InboxExecutionStore(NotificationsDbContext context)
{
    internal Task<List<InboxMessage>> ClaimAsync(Guid lease, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var until = now.AddMinutes(1);
        return context.Inbox.FromSqlInterpolated($"""
            UPDATE notifications.inbox SET state='Sending', lease_id={lease}, lease_until={until}, updated_at={now}
            WHERE message_id IN (
                SELECT message_id FROM notifications.inbox
                WHERE state IN ('Accepted','Sending') AND NOT suspended AND protected_envelope IS NOT NULL
                  AND expires_at>{now} AND attempts<5 AND (next_attempt_at IS NULL OR next_attempt_at<={now})
                  AND (lease_until IS NULL OR lease_until<={now})
                ORDER BY accepted_at FOR UPDATE SKIP LOCKED LIMIT 1)
            RETURNING *
            """).AsNoTracking().ToListAsync(cancellationToken);
    }

    internal Task<int> BeginAttemptAsync(Guid id, Guid lease, DateTimeOffset now, DateTimeOffset fallback, CancellationToken cancellationToken) =>
        context.Inbox.Where(row => row.MessageId == id && row.State == InboxState.Sending && row.LeaseId == lease
                && row.LeaseUntil > now && row.ExpiresAt > now && row.Attempts < 5 && !row.Suspended)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.Attempts, row => row.Attempts + 1)
                .SetProperty(row => row.LastAttemptAt, now).SetProperty(row => row.NextAttemptAt, fallback).SetProperty(row => row.UpdatedAt, now), cancellationToken);

    internal Task<bool> IsOwnedAsync(Guid id, Guid lease, DateTimeOffset now, CancellationToken cancellationToken) =>
        context.Inbox.AnyAsync(row => row.MessageId == id && row.State == InboxState.Sending && row.LeaseId == lease
            && row.LeaseUntil > now && row.ExpiresAt > now && !row.Suspended, cancellationToken);

    internal Task<int> SuspendAsync(Guid id, Guid lease, DateTimeOffset now, CancellationToken cancellationToken) =>
        context.Inbox.Where(row => row.MessageId == id && row.LeaseId == lease && row.State == InboxState.Sending)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.State, InboxState.Accepted).SetProperty(row => row.Suspended, true)
                .SetProperty(row => row.LastFailureCode, "protection-unavailable").SetProperty(row => row.UpdatedAt, now)
                .SetProperty(row => row.LeaseId, (Guid?)null).SetProperty(row => row.LeaseUntil, (DateTimeOffset?)null), cancellationToken);

    internal async Task FailAttemptAsync(Guid id, Guid lease, string? permanentCode, DateTimeOffset now,
        DateTimeOffset retryAt, CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var row = await context.Inbox.AsNoTracking().SingleOrDefaultAsync(item => item.MessageId == id && item.LeaseId == lease
            && item.State == InboxState.Sending, cancellationToken);
        if (row is null) { return; }
        if (row.ExpiresAt <= now)
        { await InboxPersistence.TerminalAsync(context, id, lease, InboxState.Expired, null, now, cancellationToken); }
        else if (permanentCode is not null || row.Attempts >= 5)
        {
            var code = permanentCode ?? "retry-exhausted";
            if (await InboxPersistence.TerminalAsync(context, id, lease, InboxState.Failed, code, now, cancellationToken) == 1)
            { await InboxPersistence.AddFailureAsync(context, id, code, row.Fingerprint, row.ExpiresAt, now, cancellationToken); }
            await context.SaveChangesAsync(cancellationToken);
        }
        else
        {
            var next = retryAt < row.ExpiresAt ? retryAt : row.ExpiresAt;
            await context.Inbox.Where(item => item.MessageId == id && item.LeaseId == lease && item.State == InboxState.Sending)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.State, InboxState.Accepted).SetProperty(item => item.NextAttemptAt, next)
                    .SetProperty(item => item.LastFailureCode, "provider-transient").SetProperty(item => item.UpdatedAt, now)
                    .SetProperty(item => item.LeaseId, (Guid?)null).SetProperty(item => item.LeaseUntil, (DateTimeOffset?)null), cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    internal Task<int> DeliverAsync(Guid id, Guid lease, DateTimeOffset now, CancellationToken cancellationToken) =>
        InboxPersistence.TerminalAsync(context, id, lease, InboxState.Delivered, null, now, cancellationToken);
}
