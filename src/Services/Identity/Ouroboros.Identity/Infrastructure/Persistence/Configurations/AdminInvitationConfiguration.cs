using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ouroboros.BuildingBlocks.Persistence;
using Ouroboros.Identity.Domain.Administrators;

namespace Ouroboros.Identity.Infrastructure.Persistence.Configurations;

internal sealed class AdminInvitationConfiguration : EntityConfiguration<AdminInvitation>
{
    protected override void ConfigureEntity(EntityTypeBuilder<AdminInvitation> builder)
    {
        builder.ToTable("admin_invitations");
        builder.Property(row => row.Email).HasMaxLength(254);
        builder.Property(row => row.State).HasConversion<string>().HasMaxLength(32);
        builder.Property(row => row.TokenHash).HasMaxLength(32);
        builder.HasIndex(row => row.TokenHash).IsUnique();
        builder.HasIndex(row => new { row.Email, row.State });
    }
}
