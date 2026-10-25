using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ouroboros.BuildingBlocks.Persistence;
using Ouroboros.Identity.Domain.EmailTokens;
using Ouroboros.Identity.Domain.Users;

namespace Ouroboros.Identity.Infrastructure.Persistence.Configurations;

internal sealed class EmailTokenConfiguration : EntityConfiguration<EmailToken>
{
    protected override void ConfigureEntity(EntityTypeBuilder<EmailToken> builder)
    {
        builder.ToTable("email_tokens");
        builder.Property(token => token.Purpose).HasConversion<string>().HasMaxLength(30);
        builder.Property(token => token.Email).HasMaxLength(User.EmailMaxLength);
        builder.HasOne<User>().WithMany().HasForeignKey(token => token.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(token => token.TokenHash).HasColumnType("bytea");
        builder.HasIndex(token => token.TokenHash).IsUnique();
        builder.Property(token => token.ExpiresAt).HasColumnType("timestamptz");
        builder.Property(token => token.UsedAt).HasColumnType("timestamptz");
        builder.Property(token => token.RevokedAt).HasColumnType("timestamptz");

        // Busca dos tokens pendentes de um e-mail (cadastro) ou de uma conta (redefinição, pelo índice da
        // FK user_id), para invalidá-los numa nova solicitação.
        builder.HasIndex(token => new { token.Email, token.Purpose });
    }
}
