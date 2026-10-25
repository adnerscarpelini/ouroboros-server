using Ouroboros.Modules.Identity.Contracts;

namespace Ouroboros.Modules.Identity.Application.Parameters;

// Implementação do contrato de leitura: cada chamada consulta a tabela, sem cache.
internal sealed class SystemParameters(IParameterReader reader) : ISystemParameters
{
    public async Task<string> GetAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var value = await reader.GetValueAsync(key, cancellationToken);

        return value ?? throw new ParameterNotFoundException(key);
    }
}
