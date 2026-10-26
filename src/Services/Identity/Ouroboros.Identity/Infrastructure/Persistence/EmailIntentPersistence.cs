using Microsoft.EntityFrameworkCore;
using Ouroboros.Identity.Domain.Notifications;

namespace Ouroboros.Identity.Infrastructure.Persistence;

internal static class EmailIntentPersistence
{
    internal static Task<int> CancelPendingAsync(IdentityDbContext context, Guid[] sourceTokens, string supersessionKey, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (context.Database.CurrentTransaction is null) { throw new InvalidOperationException("Cancelamento exige a transação local."); }
        return context.EmailIntents.Where(intent => intent.State == EmailIntentState.Pending
                && (sourceTokens.Contains(intent.SourceTokenId) || intent.SupersessionKey == supersessionKey))
            .ExecuteUpdateAsync(update => update.SetProperty(intent => intent.State, EmailIntentState.Cancelled)
                .SetProperty(intent => intent.ProtectedEnvelope, (byte[]?)null).SetProperty(intent => intent.ProtectionKeyId, (string?)null)
                .SetProperty(intent => intent.SupersessionKey, (string?)null).SetProperty(intent => intent.Traceparent, (string?)null)
                .SetProperty(intent => intent.Tracestate, (string?)null).SetProperty(intent => intent.UpdatedAt, now), cancellationToken);
    }

    internal static async Task MaintainAsync(IdentityDbContext context, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var expired = await context.EmailIntents.AsNoTracking().Where(intent => intent.ProtectedEnvelope != null && intent.ExpiresAt <= now)
            .OrderBy(intent => intent.Id).Select(intent => intent.Id).Take(100).ToArrayAsync(cancellationToken);
        await context.EmailIntents.Where(intent => expired.Contains(intent.Id) && intent.ExpiresAt <= now)
            .ExecuteUpdateAsync(update => update.SetProperty(intent => intent.State, EmailIntentState.Expired)
                .SetProperty(intent => intent.ProtectedEnvelope, (byte[]?)null).SetProperty(intent => intent.ProtectionKeyId, (string?)null)
                .SetProperty(intent => intent.SupersessionKey, (string?)null).SetProperty(intent => intent.Traceparent, (string?)null)
                .SetProperty(intent => intent.Tracestate, (string?)null).SetProperty(intent => intent.UpdatedAt, now), cancellationToken);
        var cutoff = now.AddDays(-30);
        var removable = await context.EmailIntents.AsNoTracking().Where(intent => intent.ExpiresAt < cutoff && intent.ProtectedEnvelope == null
                && (intent.State == EmailIntentState.Published || intent.State == EmailIntentState.Cancelled || intent.State == EmailIntentState.Expired || intent.State == EmailIntentState.Failed))
            .OrderBy(intent => intent.Id).Select(intent => intent.Id).Take(100).ToArrayAsync(cancellationToken);
        await context.EmailIntents.Where(intent => removable.Contains(intent.Id)).ExecuteDeleteAsync(cancellationToken);
    }
}
