using Microsoft.EntityFrameworkCore;
using Ouroboros.BuildingBlocks.Persistence;
using Ouroboros.Notifications.Domain;

namespace Ouroboros.Notifications.Infrastructure.Persistence;

internal sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : DbContext(options)
{
    internal const string Schema = "notifications";
    internal DbSet<InboxMessage> Inbox => Set<InboxMessage>();
    internal DbSet<SupersessionCursor> Supersessions => Set<SupersessionCursor>();
    internal DbSet<FailureDispatch> Failures => Set<FailureDispatch>();
    internal DbSet<ProcessingRecovery> Recoveries => Set<ProcessingRecovery>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        var inbox = modelBuilder.Entity<InboxMessage>();
        inbox.ToTable("inbox", table =>
        {
            table.HasCheckConstraint("ck_inbox_payload_size", "protected_envelope IS NULL OR octet_length(protected_envelope) <= 32768");
            table.HasCheckConstraint("ck_inbox_attempts", "attempts BETWEEN 0 AND 5");
        });
        inbox.HasKey(row => row.MessageId);
        inbox.Property(row => row.Fingerprint).HasMaxLength(32);
        inbox.Property(row => row.FingerprintKeyId).HasMaxLength(64);
        inbox.Property(row => row.SupersessionId).HasMaxLength(64);
        inbox.Property(row => row.State).HasConversion<string>().HasMaxLength(24);
        inbox.Property(row => row.LastFailureCode).HasMaxLength(32);
        inbox.HasIndex(row => new { row.SupersessionId, row.State });
        inbox.HasIndex(row => new { row.State, row.NextAttemptAt });
        inbox.HasIndex(row => row.ExpiresAt);
        var cursor = modelBuilder.Entity<SupersessionCursor>();
        cursor.ToTable("supersessions");
        cursor.HasKey(row => row.Id);
        cursor.Property(row => row.Id).HasMaxLength(64);
        cursor.HasIndex(row => row.RetainUntil);
        var failure = modelBuilder.Entity<FailureDispatch>();
        failure.ToTable("failure_outbox");
        failure.HasKey(row => row.Id);
        failure.Property(row => row.Code).HasMaxLength(32);
        failure.HasIndex(row => new { row.PublishedAt, row.NextAttemptAt });
        failure.HasIndex(row => row.RetainUntil);
        var recovery = modelBuilder.Entity<ProcessingRecovery>();
        recovery.ToTable("processing_recoveries");
        recovery.HasKey(row => row.Id);
        recovery.Property(row => row.Actor).HasMaxLength(128);
        recovery.HasIndex(row => row.RetainUntil);
        SnakeCaseNaming.Apply(modelBuilder);
    }
}
