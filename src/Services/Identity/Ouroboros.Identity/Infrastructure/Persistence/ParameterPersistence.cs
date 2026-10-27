using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ouroboros.Identity.Application.Parameters;
using Ouroboros.Identity.Contracts;
using Ouroboros.Identity.Domain.Notifications;
using Ouroboros.Identity.Domain.Parameters;

namespace Ouroboros.Identity.Infrastructure.Persistence;

internal sealed class ParameterPersistence(IdentityDbContext context) : IParameterPersistence
{
    public async Task ChangeWithIntentsAsync(string key, string? value, string? description, Guid changedBy, int revision,
        DateTimeOffset changedAt, IReadOnlyList<EmailIntent> intents, CancellationToken cancellationToken)
    {
        if (context.Database.CurrentTransaction is null) { throw new InvalidOperationException("A alteração administrativa exige a transação local de Identity."); }
        var parameter = await context.Parameters.SingleOrDefaultAsync(candidate => candidate.Key == key, cancellationToken)
            ?? throw new ParameterNotFoundException(key);
        if (parameter.Revision != revision) { throw new ParameterConflictException(key); }
        context.ParameterHistory.Add(parameter.Change(value, description, changedBy, changedAt));
        context.EmailIntents.AddRange(intents);
        await SaveAsync(key, cancellationToken);
    }

    public async Task CreateAsync(Parameter parameter, ParameterHistory history, CancellationToken cancellationToken)
    {
        context.Parameters.Add(parameter);
        context.ParameterHistory.Add(history);
        await SaveAsync(parameter.Key, cancellationToken);
    }

    public async Task ChangeAsync(
        string key,
        string? value,
        string? description,
        Guid changedBy,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken)
    {
        var parameter = await context.Parameters.SingleOrDefaultAsync(candidate => candidate.Key == key, cancellationToken)
            ?? throw new ParameterNotFoundException(key);

        context.ParameterHistory.Add(parameter.Change(value, description, changedBy, changedAt));
        await SaveAsync(key, cancellationToken);
    }

    // Um único SaveChanges mantém revisão, autor, valor e histórico na transação local do EF.
    private async Task SaveAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new ParameterConflictException(key, exception);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new ParameterConflictException(key, exception);
        }
    }
}
