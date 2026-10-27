using Ouroboros.Identity.Domain.Notifications;
using Ouroboros.Identity.Domain.Parameters;

namespace Ouroboros.Identity.Application.Parameters;

// Cada operação confirma parâmetro e histórico juntos; não expõe operações de persistência parciais.
internal interface IParameterPersistence
{
    Task CreateAsync(Parameter parameter, ParameterHistory history, CancellationToken cancellationToken);
    Task ChangeWithIntentsAsync(string key, string? value, string? description, Guid changedBy, int revision,
        DateTimeOffset changedAt, IReadOnlyList<EmailIntent> intents, CancellationToken cancellationToken);

    Task ChangeAsync(
        string key,
        string? value,
        string? description,
        Guid changedBy,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken);
}
