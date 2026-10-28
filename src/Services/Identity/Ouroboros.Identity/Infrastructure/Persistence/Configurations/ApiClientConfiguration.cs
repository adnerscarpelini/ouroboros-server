using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ouroboros.BuildingBlocks.Persistence;
using Ouroboros.Identity.Domain.ApiClients;

namespace Ouroboros.Identity.Infrastructure.Persistence.Configurations;

internal sealed class ApiClientConfiguration : EntityConfiguration<ApiClient>
{
    protected override void ConfigureEntity(EntityTypeBuilder<ApiClient> builder)
    {
        builder.ToTable("api_clients");
        builder.Property(client => client.Name).HasMaxLength(100);
        builder.Property(client => client.Description).HasMaxLength(1000);
        builder.Property(client => client.SecretHash).HasMaxLength(32);
    }
}
