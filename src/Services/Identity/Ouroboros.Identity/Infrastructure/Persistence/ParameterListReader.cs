using Microsoft.EntityFrameworkCore;
using Ouroboros.Identity.Application.Parameters;
using Ouroboros.Identity.Contracts.Parameters;

namespace Ouroboros.Identity.Infrastructure.Persistence;

internal sealed class ParameterListReader(IdentityDbContext context) : IParameterListReader
{
    public async Task<IReadOnlyList<ParameterView>> ReadAsync(Guid? after, int count, CancellationToken cancellationToken)
    {
        var query = context.Parameters.AsNoTracking();
        if (after is { } position) { query = query.Where(parameter => parameter.ExternalId.CompareTo(position) > 0); }
        return await query.OrderBy(parameter => parameter.ExternalId).Take(count)
            .Select(parameter => new ParameterView(parameter.ExternalId, parameter.Key, parameter.Value, parameter.Description,
                parameter.DataType.ToString(), parameter.ValidationPattern, parameter.IsSensitive, parameter.Revision, parameter.UpdatedBy, parameter.UpdatedAt))
            .ToArrayAsync(cancellationToken);
    }
}
