using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Ouroboros.Modules.Settings.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "settings");

            migrationBuilder.CreateTable(
                name: "parameter_history",
                schema: "settings",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    parameter_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    previous_value = table.Column<string>(type: "text", nullable: true),
                    new_value = table.Column<string>(type: "text", nullable: false),
                    previous_description = table.Column<string>(type: "text", nullable: true),
                    new_description = table.Column<string>(type: "text", nullable: false),
                    data_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    validation_pattern = table.Column<string>(type: "text", nullable: true),
                    is_sensitive = table.Column<bool>(type: "boolean", nullable: false),
                    changed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    external_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_parameter_history", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "parameters",
                schema: "settings",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    value = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    data_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    validation_pattern = table.Column<string>(type: "text", nullable: true),
                    is_sensitive = table.Column<bool>(type: "boolean", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    external_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_parameters", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_parameter_history_external_id",
                schema: "settings",
                table: "parameter_history",
                column: "external_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_parameter_history_parameter_key_revision",
                schema: "settings",
                table: "parameter_history",
                columns: new[] { "parameter_key", "revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_parameters_external_id",
                schema: "settings",
                table: "parameters",
                column: "external_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_parameters_key",
                schema: "settings",
                table: "parameters",
                column: "key",
                unique: true);

            // Valores iniciais. As linhas do histórico registram a inclusão (revisão 0, sem autor),
            // como qualquer outra gravação de parâmetro. uuidv7() existe a partir do PostgreSQL 18.
            migrationBuilder.Sql("""
                INSERT INTO settings.parameters
                    (external_id, key, value, description, data_type, validation_pattern, is_sensitive, revision, updated_by, created_at, updated_at)
                VALUES
                    (uuidv7(), 'Identity:AdminInvitations:AllowedEmailDomain', 'adnerscarpelini.com',
                     'Domínio de e-mail obrigatório para contas de administrador. Usado na criação do primeiro administrador por comando de linha (#11), no convite de administradores (#25) e na validação dos aprovadores de convites (#36). Formato: apenas o domínio, sem @, em minúsculas (por exemplo, adnerscarpelini.com). A comparação é exata e não aceita subdomínios. Alterar este valor não afeta administradores existentes, apenas novas criações e convites.',
                     'Domain', NULL, true, 0, NULL, now(), now()),
                    (uuidv7(), 'Identity:AdminInvitations:ApproverEmails', 'oi@adnerscarpelini.com',
                     'E-mails dos administradores que aprovam convites de novos administradores (#36), separados por vírgula. Cada e-mail precisa pertencer a um administrador ativo e ao domínio de Identity:AdminInvitations:AllowedEmailDomain; quem cria um convite nunca pode aprová-lo. Alterar este valor muda quem recebe e pode decidir os convites pendentes a partir da próxima leitura.',
                     'EmailList', NULL, true, 0, NULL, now(), now());

                INSERT INTO settings.parameter_history
                    (external_id, parameter_key, revision, previous_value, new_value, previous_description, new_description,
                     data_type, validation_pattern, is_sensitive, changed_by, changed_at, created_at, updated_at)
                SELECT uuidv7(), key, 0, NULL, value, NULL, description,
                       data_type, validation_pattern, is_sensitive, NULL, now(), now(), now()
                FROM settings.parameters;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "parameter_history",
                schema: "settings");

            migrationBuilder.DropTable(
                name: "parameters",
                schema: "settings");
        }
    }
}
