using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Ouroboros.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminInvitations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO identity.parameters
                    (external_id, key, value, description, data_type, validation_pattern, is_sensitive, revision, created_at, updated_at)
                VALUES
                    (uuidv7(), 'Identity:AdminInvitations:TokenLifetimeHours', '24',
                     'Validade de novos links de convite, em horas, de 1 a 168. Não altera links já emitidos.',
                     'Integer', '[1-9]|[1-9][0-9]|1[0-5][0-9]|16[0-8]', true, 0, now(), now()),
                    (uuidv7(), 'Identity:AdminInvitations:CompletionUrl', 'http://localhost:3001/administradores/convite',
                     'URL da tela administrativa de conclusão. Configurar a interface interna; sem query ou fragmento.',
                     'String', 'https?://[^\s?#]+', true, 0, now(), now());
                INSERT INTO identity.parameter_history
                    (external_id, parameter_key, revision, previous_value, new_value, previous_description, new_description,
                     data_type, validation_pattern, is_sensitive, changed_by, changed_at, created_at, updated_at)
                SELECT uuidv7(), key, 0, NULL, value, NULL, description, data_type, validation_pattern, is_sensitive, NULL, now(), now(), now()
                FROM identity.parameters WHERE key IN ('Identity:AdminInvitations:TokenLifetimeHours', 'Identity:AdminInvitations:CompletionUrl');
                """);
            migrationBuilder.AddColumn<Guid>(
                name: "invited_by",
                schema: "identity",
                table: "users",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "admin_invitations",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    invited_by = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    approval_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: true),
                    token_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decided_by = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    external_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_admin_invitations", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_admin_invitations_email_state",
                schema: "identity",
                table: "admin_invitations",
                columns: new[] { "email", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_admin_invitations_external_id",
                schema: "identity",
                table: "admin_invitations",
                column: "external_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_admin_invitations_token_hash",
                schema: "identity",
                table: "admin_invitations",
                column: "token_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM identity.parameter_history WHERE parameter_key IN ('Identity:AdminInvitations:TokenLifetimeHours', 'Identity:AdminInvitations:CompletionUrl');
                DELETE FROM identity.parameters WHERE key IN ('Identity:AdminInvitations:TokenLifetimeHours', 'Identity:AdminInvitations:CompletionUrl');
                """);
            migrationBuilder.DropTable(
                name: "admin_invitations",
                schema: "identity");

            migrationBuilder.DropColumn(
                name: "invited_by",
                schema: "identity",
                table: "users");
        }
    }
}
