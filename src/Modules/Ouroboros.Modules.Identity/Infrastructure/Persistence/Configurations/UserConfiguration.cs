using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ouroboros.BuildingBlocks.Persistence;
using Ouroboros.Modules.Identity.Domain.Users;

namespace Ouroboros.Modules.Identity.Infrastructure.Persistence.Configurations;

internal sealed class UserConfiguration : EntityConfiguration<User>
{
    protected override void ConfigureEntity(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");
        builder.Property(user => user.Email).HasMaxLength(User.EmailMaxLength);
        builder.HasIndex(user => user.Email).IsUnique();
        builder.Property(user => user.PasswordHash);
        builder.Property(user => user.Role).HasConversion<string>().HasMaxLength(20);
        builder.Property(user => user.IsActive);
        builder.Property(user => user.FailedLoginCount);
        builder.Property(user => user.LockedUntil).HasColumnType("timestamptz");
    }
}
