using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ouroboros.IntegrationTests;

// Os módulos ainda não têm endpoints. Esta sonda, registrada só nos testes, prova que a API sobe,
// conecta ao banco e enxerga as migrations aplicadas de cada módulo.
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/integration-probe")]
internal sealed class MigrationsProbeController(IServiceProvider services) : ControllerBase
{
    [HttpGet("migrations")]
    public async Task<Dictionary<string, string[]>> GetAppliedMigrations(CancellationToken cancellationToken)
    {
        var applied = new Dictionary<string, string[]>();
        foreach (var context in ModuleDbContexts.Resolve(services))
        {
            applied[context.GetType().Name] = [.. await context.Database.GetAppliedMigrationsAsync(cancellationToken)];
        }

        return applied;
    }
}
