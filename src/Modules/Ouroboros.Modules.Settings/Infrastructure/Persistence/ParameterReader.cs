using Microsoft.EntityFrameworkCore;
using Ouroboros.Modules.Settings.Application.Parameters;

namespace Ouroboros.Modules.Settings.Infrastructure.Persistence;

internal sealed class ParameterReader(SettingsDbContext context) : IParameterReader
{
    public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken) =>
        context.Parameters.AsNoTracking()
            .Where(parameter => parameter.Key == key)
            .Select(parameter => parameter.Value)
            .SingleOrDefaultAsync(cancellationToken);
}
