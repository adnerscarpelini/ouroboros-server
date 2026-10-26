using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ouroboros.Notifications.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialNotificationsInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "notifications");

            migrationBuilder.CreateTable(
                name: "failure_outbox",
                schema: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    original_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    retain_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_id = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    suspended = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_failure_outbox", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "inbox",
                schema: "notifications",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    fingerprint = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    fingerprint_key_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    protected_envelope = table.Column<byte[]>(type: "bytea", nullable: true),
                    supersession_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    state = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    suspended = table.Column<bool>(type: "boolean", nullable: false),
                    last_failure_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    lease_id = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inbox", x => x.message_id);
                    table.CheckConstraint("ck_inbox_attempts", "attempts BETWEEN 0 AND 5");
                    table.CheckConstraint("ck_inbox_payload_size", "protected_envelope IS NULL OR octet_length(protected_envelope) <= 32768");
                });

            migrationBuilder.CreateTable(
                name: "processing_recoveries",
                schema: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    retain_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_processing_recoveries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "supersessions",
                schema: "notifications",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    last_occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    retain_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_supersessions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_failure_outbox_published_at_next_attempt_at",
                schema: "notifications",
                table: "failure_outbox",
                columns: new[] { "published_at", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "ix_failure_outbox_retain_until",
                schema: "notifications",
                table: "failure_outbox",
                column: "retain_until");

            migrationBuilder.CreateIndex(
                name: "ix_inbox_expires_at",
                schema: "notifications",
                table: "inbox",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_inbox_state_next_attempt_at",
                schema: "notifications",
                table: "inbox",
                columns: new[] { "state", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "ix_inbox_supersession_id_state",
                schema: "notifications",
                table: "inbox",
                columns: new[] { "supersession_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_processing_recoveries_retain_until",
                schema: "notifications",
                table: "processing_recoveries",
                column: "retain_until");

            migrationBuilder.CreateIndex(
                name: "ix_supersessions_retain_until",
                schema: "notifications",
                table: "supersessions",
                column: "retain_until");
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname='notifications_runtime') THEN
                        REVOKE ALL ON notifications."__EFMigrationsHistory" FROM notifications_runtime;
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "failure_outbox",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "inbox",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "processing_recoveries",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "supersessions",
                schema: "notifications");
        }
    }
}
