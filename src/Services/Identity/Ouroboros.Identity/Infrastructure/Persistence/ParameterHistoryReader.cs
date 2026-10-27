using Microsoft.EntityFrameworkCore;
using Ouroboros.Identity.Application.Parameters;
using Ouroboros.Identity.Contracts;
using Ouroboros.Identity.Contracts.Parameters;

namespace Ouroboros.Identity.Infrastructure.Persistence;

internal sealed class ParameterHistoryReader(IdentityDbContext context) : IParameterHistoryReader
{
    public async Task<IReadOnlyList<ParameterHistoryView>> ReadAsync(string key, ParameterCursor.Cursor? after, int count, CancellationToken cancellationToken)
    {
        if (!await context.Parameters.AnyAsync(parameter => parameter.Key == key, cancellationToken))
        { throw new ParameterNotFoundException(key); }
        var query = context.ParameterHistory.AsNoTracking().Where(history => history.ParameterKey == key);
        if (after is not null)
        {
            query = query.Where(history => history.ChangedAt < after.ChangedAt
                || (history.ChangedAt == after.ChangedAt && history.ExternalId.CompareTo(after.Position) < 0));
        }
        return await query.OrderByDescending(history => history.ChangedAt).ThenByDescending(history => history.ExternalId).Take(count)
            .Select(history => new ParameterHistoryView(history.ExternalId, history.ParameterKey, history.Revision,
                history.PreviousValue, history.NewValue, history.PreviousDescription, history.NewDescription, history.DataType.ToString(),
                history.ValidationPattern, history.IsSensitive, history.ChangedBy, history.ChangedAt)).ToArrayAsync(cancellationToken);
    }
}
