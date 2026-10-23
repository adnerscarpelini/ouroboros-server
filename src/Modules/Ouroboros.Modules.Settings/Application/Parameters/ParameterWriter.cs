using Microsoft.EntityFrameworkCore;
using Ouroboros.Modules.Settings.Contracts;
using Ouroboros.Modules.Settings.Domain.Parameters;
using Ouroboros.Modules.Settings.Infrastructure.Persistence;

namespace Ouroboros.Modules.Settings.Application.Parameters;

// Grava parâmetros e o histórico de alterações na mesma transação (um único SaveChanges). Base para a
// alteração pelo backoffice (#34).
internal sealed class ParameterWriter(SettingsDbContext context, TimeProvider timeProvider)
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
        context.Parameters.Add(parameter);
        context.ParameterHistory.Add(history);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task ChangeAsync(
        string key,
        string? value,
        string? description,
        Guid changedBy,
        CancellationToken cancellationToken)
    {
        var parameter = await context.Parameters.SingleOrDefaultAsync(candidate => candidate.Key == key, cancellationToken)
            ?? throw new ParameterNotFoundException(key);

        context.ParameterHistory.Add(parameter.Change(value, description, changedBy, timeProvider.GetUtcNow()));
        await context.SaveChangesAsync(cancellationToken);
    }
}
