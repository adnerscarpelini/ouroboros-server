using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ouroboros.Modules.Identity.Domain;

namespace Ouroboros.Modules.Identity.Infrastructure.Persistence;

// Mapeamento comum das entidades: Id bigint identity como chave, ExternalId uuid com índice único
// e datas em timestamptz. As colunas ficam em snake_case pela convenção do DbContext.
internal abstract class EntityConfiguration<TEntity> : IEntityTypeConfiguration<TEntity>
    where TEntity : Entity
{
    public void Configure(EntityTypeBuilder<TEntity> builder)
    {
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).UseIdentityAlwaysColumn();
        builder.Property(entity => entity.ExternalId).HasColumnType("uuid").ValueGeneratedNever();
        builder.HasIndex(entity => entity.ExternalId).IsUnique();
        builder.Property(entity => entity.CreatedAt).HasColumnType("timestamptz");
        builder.Property(entity => entity.UpdatedAt).HasColumnType("timestamptz");

        ConfigureEntity(builder);
    }

    protected abstract void ConfigureEntity(EntityTypeBuilder<TEntity> builder);
}
