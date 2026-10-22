using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Ouroboros.Modules.Identity.Domain;

namespace Ouroboros.Modules.Identity.Infrastructure.Persistence;

// Preenche CreatedAt e UpdatedAt das entidades a partir do TimeProvider, em UTC, antes de gravar.
internal sealed class AuditTimestampsInterceptor(TimeProvider timeProvider) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Stamp(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Stamp(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Stamp(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();

        // Alterar só um owned type (value object) deixa a entrada do dono como Unchanged; o dono
        // conta como alterado para fins de UpdatedAt.
        var ownersOfChangedOwned = context.ChangeTracker.Entries()
            .Where(entry => entry.Metadata.IsOwned() && entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(entry => FindRootOwner(context, entry)?.Entity)
            .OfType<Entity>()
            .ToHashSet(ReferenceEqualityComparer.Instance);

        foreach (var entry in context.ChangeTracker.Entries<Entity>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Property(entity => entity.CreatedAt).CurrentValue = now;
                entry.Property(entity => entity.UpdatedAt).CurrentValue = now;
            }
            else if (entry.State == EntityState.Modified || (entry.State == EntityState.Unchanged && ownersOfChangedOwned.Contains(entry.Entity)))
            {
                entry.Property(entity => entity.CreatedAt).IsModified = false;
                entry.Property(entity => entity.UpdatedAt).CurrentValue = now;
            }
        }
    }

    // Sobe pela ownership (inclusive owned aninhados) até a entrada da entidade dona, comparando a
    // chave estrangeira do owned com a chave do dono entre as entradas rastreadas.
    private static EntityEntry? FindRootOwner(DbContext context, EntityEntry owned)
    {
        var ownership = owned.Metadata.FindOwnership();
        if (ownership is null)
        {
            return owned;
        }

        var foreignKey = ownership.Properties
            .Select(property => owned.State == EntityState.Added
                ? owned.Property(property.Name).CurrentValue
                : owned.Property(property.Name).OriginalValue)
            .ToArray();

        var owner = context.ChangeTracker.Entries().FirstOrDefault(entry =>
            ownership.PrincipalEntityType.IsAssignableFrom(entry.Metadata)
            && ownership.PrincipalKey.Properties
                .Select(property => entry.Property(property.Name).CurrentValue)
                .SequenceEqual(foreignKey));

        return owner is null ? null : FindRootOwner(context, owner);
    }
}
