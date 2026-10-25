using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ouroboros.BuildingBlocks.Persistence;
using Ouroboros.Identity.Domain.Sessions;
using Ouroboros.Identity.Domain.Users;

namespace Ouroboros.Identity.Infrastructure.Persistence.Configurations;

internal sealed class SessionConfiguration : EntityConfiguration<Session>
{
    protected override void ConfigureEntity(EntityTypeBuilder<Session> builder)
    {
        builder.ToTable("sessions");
        builder.HasOne<User>().WithMany().HasForeignKey(session => session.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(session => session.UserId);
        builder.HasMany(session => session.RefreshTokens).WithOne().HasForeignKey(token => token.SessionId);
        builder.Navigation(session => session.RefreshTokens).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class RefreshTokenConfiguration : EntityConfiguration<RefreshToken>
{
    protected override void ConfigureEntity(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("refresh_tokens");
        builder.Property(token => token.TokenHash).HasColumnType("bytea");
        builder.HasIndex(token => token.TokenHash).IsUnique();
        builder.Property(token => token.ExpiresAt).HasColumnType("timestamptz");
        builder.Property(token => token.RevokedAt).HasColumnType("timestamptz");
        builder.HasIndex(token => token.SessionId);
    }
}
