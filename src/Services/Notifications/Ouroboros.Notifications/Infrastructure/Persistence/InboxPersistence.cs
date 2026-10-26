using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Ouroboros.Notifications.Domain;

namespace Ouroboros.Notifications.Infrastructure.Persistence;

internal static class InboxPersistence
{
    internal static DateTimeOffset RetainUntil(DateTimeOffset expiresAt) =>
        expiresAt > DateTimeOffset.MaxValue.AddDays(-30) ? DateTimeOffset.MaxValue : expiresAt.AddDays(30);

    internal static Task<int> TerminalAsync(NotificationsDbContext context, Guid id, Guid? lease, InboxState state, string? code,
        DateTimeOffset now, CancellationToken cancellationToken) =>
        context.Inbox.Where(row => row.MessageId == id && (row.State == InboxState.Accepted || row.State == InboxState.Sending)
                && (lease == null || row.LeaseId == lease))
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.State, state).SetProperty(row => row.LastFailureCode, code)
                .SetProperty(row => row.UpdatedAt, now).SetProperty(row => row.ProtectedEnvelope, (byte[]?)null)
                .SetProperty(row => row.SupersessionId, (string?)null).SetProperty(row => row.Suspended, false)
                .SetProperty(row => row.LeaseId, (Guid?)null).SetProperty(row => row.LeaseUntil, (DateTimeOffset?)null)
                .SetProperty(row => row.NextAttemptAt, (DateTimeOffset?)null)
                .SetProperty(row => row.DeliveredAt, state == InboxState.Delivered ? now : (DateTimeOffset?)null), cancellationToken);

    internal static Task<int> CancelOlderAsync(NotificationsDbContext context, string alias, DateTimeOffset now, CancellationToken cancellationToken) =>
        context.Inbox.Where(row => row.SupersessionId == alias && (row.State == InboxState.Accepted || row.State == InboxState.Sending))
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.State, InboxState.Cancelled)
                .SetProperty(row => row.UpdatedAt, now).SetProperty(row => row.ProtectedEnvelope, (byte[]?)null)
                .SetProperty(row => row.SupersessionId, (string?)null).SetProperty(row => row.Suspended, false)
                .SetProperty(row => row.LeaseId, (Guid?)null).SetProperty(row => row.LeaseUntil, (DateTimeOffset?)null)
                .SetProperty(row => row.NextAttemptAt, (DateTimeOffset?)null).SetProperty(row => row.LastFailureCode, (string?)null), cancellationToken);

    internal static async Task AddFailureAsync(NotificationsDbContext context, Guid id, string code, byte[] fingerprint,
        DateTimeOffset expiry, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (context.Database.CurrentTransaction is null) { throw new InvalidOperationException("Classificação exige transação local."); }
        var retention = RetainUntil(expiry);
        if (retention <= now) { return; }
        var material = Encoding.UTF8.GetBytes(id.ToString("N") + ":" + code + ":" + Convert.ToHexString(fingerprint));
        var failureId = new Guid(SHA256.HashData(material).AsSpan(0, 16));
        if (await context.Failures.AnyAsync(row => row.Id == failureId, cancellationToken)) { return; }
        context.Failures.Add(new FailureDispatch
        { Id = failureId, MessageId = id, Code = code, OccurredAt = now, OriginalExpiresAt = expiry, RetainUntil = retention });
    }

    internal static async Task MaintainAsync(NotificationsDbContext context, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var expired = await context.Inbox.AsNoTracking().Where(row => row.ProtectedEnvelope != null && row.ExpiresAt <= now)
            .OrderBy(row => row.ExpiresAt).Select(row => row.MessageId).Take(100).ToArrayAsync(cancellationToken);
        foreach (var id in expired) { await TerminalAsync(context, id, null, InboxState.Expired, null, now, cancellationToken); }
        var exhausted = await context.Inbox.AsNoTracking().Where(row => row.State == InboxState.Sending && row.Attempts >= 5
                && row.LeaseUntil <= now && row.ExpiresAt > now).Take(100).ToListAsync(cancellationToken);
        foreach (var row in exhausted)
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            if (await TerminalAsync(context, row.MessageId, row.LeaseId, InboxState.Failed, "retry-exhausted", now, cancellationToken) == 1)
            {
                await AddFailureAsync(context, row.MessageId, "retry-exhausted", row.Fingerprint, row.ExpiresAt, now, cancellationToken);
                await context.SaveChangesAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        var cutoff = now.AddDays(-30);
        var tombstones = await context.Inbox.Where(row => row.ProtectedEnvelope == null && row.ExpiresAt < cutoff
                && row.State != InboxState.Accepted && row.State != InboxState.Sending)
            .OrderBy(row => row.ExpiresAt).Select(row => row.MessageId).Take(100).ToArrayAsync(cancellationToken);
        await context.Inbox.Where(row => tombstones.Contains(row.MessageId)).ExecuteDeleteAsync(cancellationToken);
        var cursors = await context.Supersessions.Where(row => row.RetainUntil <= now).Select(row => row.Id).Take(100).ToArrayAsync(cancellationToken);
        await context.Supersessions.Where(row => cursors.Contains(row.Id)).ExecuteDeleteAsync(cancellationToken);
        var failures = await context.Failures.Where(row => row.RetainUntil <= now).Select(row => row.Id).Take(100).ToArrayAsync(cancellationToken);
        await context.Failures.Where(row => failures.Contains(row.Id)).ExecuteDeleteAsync(cancellationToken);
        var recoveries = await context.Recoveries.Where(row => row.RetainUntil <= now).Select(row => row.Id).Take(100).ToArrayAsync(cancellationToken);
        await context.Recoveries.Where(row => recoveries.Contains(row.Id)).ExecuteDeleteAsync(cancellationToken);
    }
}
