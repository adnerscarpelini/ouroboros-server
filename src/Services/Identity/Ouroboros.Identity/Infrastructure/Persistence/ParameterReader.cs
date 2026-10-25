using Microsoft.EntityFrameworkCore;
using Ouroboros.Identity.Application.Parameters;

namespace Ouroboros.Identity.Infrastructure.Persistence;

internal sealed class ParameterReader(IdentityDbContext context) : IParameterReader
{
    public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken) =>
        context.Parameters.AsNoTracking()
            .Where(parameter => parameter.Key == key)
            .Select(parameter => parameter.Value)
            .SingleOrDefaultAsync(cancellationToken);
}
