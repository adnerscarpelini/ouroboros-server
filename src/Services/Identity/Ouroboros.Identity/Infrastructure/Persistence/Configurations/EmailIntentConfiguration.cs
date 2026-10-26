using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ouroboros.Identity.Domain.Notifications;

namespace Ouroboros.Identity.Infrastructure.Persistence.Configurations;

internal sealed class EmailIntentConfiguration : IEntityTypeConfiguration<EmailIntent>
{
    public void Configure(EntityTypeBuilder<EmailIntent> builder)
    {
        builder.ToTable("email_intents", table => table.HasCheckConstraint("ck_email_intent_payload_size", "protected_envelope IS NULL OR octet_length(protected_envelope) <= 32768"));
        builder.HasKey(intent => intent.Id);
        builder.Property(intent => intent.Id).UseIdentityAlwaysColumn();
        builder.HasIndex(intent => intent.ExternalId).IsUnique();
        builder.Property(intent => intent.Type).HasMaxLength(128);
        builder.Property(intent => intent.State).HasConversion<string>().HasMaxLength(24);
        builder.Property(intent => intent.SupersessionKey).HasMaxLength(128);
        builder.Property(intent => intent.ProtectionKeyId).HasMaxLength(128);
        builder.Property(intent => intent.LastFailureCode).HasMaxLength(32);
        builder.Property(intent => intent.Traceparent).HasMaxLength(55);
        builder.Property(intent => intent.Tracestate).HasMaxLength(512);
        builder.HasIndex(intent => new { intent.SupersessionKey, intent.State }).IsUnique().HasFilter("state = 'Pending'");
        builder.HasIndex(intent => intent.SourceTokenId);
        builder.HasIndex(intent => new { intent.State, intent.ExpiresAt });
    }
}
