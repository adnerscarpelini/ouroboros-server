using Ouroboros.Modules.Identity.Domain.Parameters;

namespace Ouroboros.Modules.Identity.Application.Parameters;

// A porta confirma cada alteração junto com seu histórico. Base para o backoffice (#34).
internal sealed class ParameterWriter(IParameterPersistence persistence, TimeProvider timeProvider)
{
    public async Task CreateAsync(
        string key,
        string value,
        string description,
        ParameterDataType dataType,
        string? validationPattern,
        bool isSensitive,
        Guid? createdBy,
        CancellationToken cancellationToken)
    {
        var (parameter, history) = Parameter.Create(
            key, value, description, dataType, validationPattern, isSensitive, createdBy, timeProvider.GetUtcNow());
        await persistence.CreateAsync(parameter, history, cancellationToken);
    }

    public Task ChangeAsync(
        string key,
        string? value,
        string? description,
        Guid changedBy,
        CancellationToken cancellationToken)
        => persistence.ChangeAsync(key, value, description, changedBy, timeProvider.GetUtcNow(), cancellationToken);
}
