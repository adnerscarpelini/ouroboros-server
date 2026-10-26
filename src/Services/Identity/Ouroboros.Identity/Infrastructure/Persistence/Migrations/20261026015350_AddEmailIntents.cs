using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Ouroboros.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailIntents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "email_intents",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    supersession_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    traceparent = table.Column<string>(type: "character varying(55)", maxLength: 55, nullable: true),
                    tracestate = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    protected_envelope = table.Column<byte[]>(type: "bytea", nullable: true),
                    protection_key_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    state = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    source_token_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_email_intents", x => x.id);
                    table.CheckConstraint("ck_email_intent_payload_size", "protected_envelope IS NULL OR octet_length(protected_envelope) <= 32768");
                });

            migrationBuilder.CreateIndex(
                name: "ix_email_intents_external_id",
                schema: "identity",
                table: "email_intents",
                column: "external_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_email_intents_source_token_id",
                schema: "identity",
                table: "email_intents",
                column: "source_token_id");

            migrationBuilder.CreateIndex(
                name: "ix_email_intents_state_expires_at",
                schema: "identity",
                table: "email_intents",
                columns: new[] { "state", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_email_intents_supersession_key_state",
                schema: "identity",
                table: "email_intents",
                columns: new[] { "supersession_key", "state" },
                unique: true,
                filter: "state = 'Pending'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "email_intents",
                schema: "identity");
        }
    }
}
