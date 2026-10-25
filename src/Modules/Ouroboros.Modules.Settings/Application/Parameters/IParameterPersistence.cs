using Ouroboros.Modules.Settings.Domain.Parameters;

namespace Ouroboros.Modules.Settings.Application.Parameters;

// Cada operação confirma parâmetro e histórico juntos; não expõe operações de persistência parciais.
internal interface IParameterPersistence
{
    Task CreateAsync(Parameter parameter, ParameterHistory history, CancellationToken cancellationToken);

    Task ChangeAsync(
        string key,
        string? value,
        string? description,
        Guid changedBy,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken);
}
