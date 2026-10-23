using Microsoft.EntityFrameworkCore;
using Ouroboros.Modules.Settings.Contracts;
using Ouroboros.Modules.Settings.Infrastructure.Persistence;

namespace Ouroboros.Modules.Settings.Application.Parameters;

// Implementação do contrato de leitura: cada chamada consulta a tabela, sem cache.
internal sealed class SystemParameters(SettingsDbContext context) : ISystemParameters
{
    public async Task<string> GetAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var value = await context.Parameters.AsNoTracking()
            .Where(parameter => parameter.Key == key)
            .Select(parameter => parameter.Value)
            .SingleOrDefaultAsync(cancellationToken);

        return value ?? throw new ParameterNotFoundException(key);
    }
}
