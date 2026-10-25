using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Ouroboros.Modules.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OwnIdentityParameters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "parameter_history",
                schema: "identity",
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
                schema: "identity",
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
                schema: "identity",
                table: "parameter_history",
                column: "external_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_parameter_history_parameter_key_revision",
                schema: "identity",
                table: "parameter_history",
                columns: new[] { "parameter_key", "revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_parameters_external_id",
                schema: "identity",
                table: "parameters",
                column: "external_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_parameters_key",
                schema: "identity",
                table: "parameters",
                column: "key",
                unique: true);

            // A cópia e sua validação participam da transação da migration. A origem permanece intacta.
            migrationBuilder.Sql("""
                DO $migration$
                BEGIN
                    IF to_regclass('settings.parameters') IS NULL AND to_regclass('settings.parameter_history') IS NULL THEN
                INSERT INTO identity.parameters
                                    (external_id, key, value, description, data_type, validation_pattern, is_sensitive, revision, updated_by, created_at, updated_at)
                                VALUES
                                    (uuidv7(), 'Identity:AdminInvitations:AllowedEmailDomain', 'adnerscarpelini.com',
                                     'Domínio de e-mail obrigatório para contas de administrador. Usado na criação do primeiro administrador por comando de linha (#11), no convite de administradores (#25) e na validação dos aprovadores de convites (#36). Formato: apenas o domínio, sem @, em minúsculas (por exemplo, adnerscarpelini.com). A comparação é exata e não aceita subdomínios. Alterar este valor não afeta administradores existentes, apenas novas criações e convites.',
                                     'Domain', NULL, true, 0, NULL, now(), now()),
                                    (uuidv7(), 'Identity:AdminInvitations:ApproverEmails', 'oi@adnerscarpelini.com',
                                     'E-mails dos administradores que aprovam convites de novos administradores (#36), separados por vírgula. Cada e-mail precisa pertencer a um administrador ativo e ao domínio de Identity:AdminInvitations:AllowedEmailDomain; quem cria um convite nunca pode aprová-lo. Alterar este valor muda quem recebe e pode decidir os convites pendentes a partir da próxima leitura.',
                                     'EmailList', NULL, true, 0, NULL, now(), now());

                                INSERT INTO identity.parameter_history
                                    (external_id, parameter_key, revision, previous_value, new_value, previous_description, new_description,
                                     data_type, validation_pattern, is_sensitive, changed_by, changed_at, created_at, updated_at)
                                SELECT uuidv7(), key, 0, NULL, value, NULL, description,
                                       data_type, validation_pattern, is_sensitive, NULL, now(), now(), now()
                                FROM identity.parameters;
                INSERT INTO identity.parameters
                                    (external_id, key, value, description, data_type, validation_pattern, is_sensitive, revision, updated_by, created_at, updated_at)
                                VALUES
                                    (uuidv7(), 'Identity:Lockout:MaxFailedAttempts', '5',
                                     'Quantidade de falhas de senha seguidas que bloqueia a conta no login (#13). Ao atingir o limite, a conta fica bloqueada pelo tempo de Identity:Lockout:DurationMinutes e o contador volta a zero; um login bem-sucedido antes do limite também zera o contador. Formato: inteiro de 1 a 99. Alterar este valor vale a partir da próxima falha de senha e não libera contas já bloqueadas.',
                                     'Integer', '[1-9][0-9]?', false, 0, NULL, now(), now()),
                                    (uuidv7(), 'Identity:Lockout:DurationMinutes', '15',
                                     'Duração, em minutos, do bloqueio da conta após Identity:Lockout:MaxFailedAttempts falhas de senha seguidas (#13). Durante o bloqueio o login responde o mesmo 401 de senha errada, mesmo com a senha correta. O bloqueio expira sozinho. Formato: inteiro de 1 a 1440. Alterar este valor vale para os próximos bloqueios e não muda o fim dos bloqueios em andamento.',
                                     'Integer', '[1-9][0-9]{0,2}|1[0-3][0-9]{2}|14[0-3][0-9]|1440', false, 0, NULL, now(), now());

                                INSERT INTO identity.parameter_history
                                    (external_id, parameter_key, revision, previous_value, new_value, previous_description, new_description,
                                     data_type, validation_pattern, is_sensitive, changed_by, changed_at, created_at, updated_at)
                                SELECT uuidv7(), key, 0, NULL, value, NULL, description,
                                       data_type, validation_pattern, is_sensitive, NULL, now(), now(), now()
                                FROM identity.parameters
                                WHERE key IN ('Identity:Lockout:MaxFailedAttempts', 'Identity:Lockout:DurationMinutes');
                INSERT INTO identity.parameters
                                    (external_id, key, value, description, data_type, validation_pattern, is_sensitive, revision, updated_by, created_at, updated_at)
                                VALUES
                                    (uuidv7(), 'Identity:Registration:TokenLifetimeHours', '24',
                                     'Validade, em horas, do link de cadastro de cliente enviado por e-mail (#20). Depois desse prazo o link não conclui o cadastro (#21) e a pessoa precisa solicitar de novo. Formato: inteiro de 1 a 168. Alterar este valor vale para os próximos links; os já enviados mantêm a validade com que foram gerados.',
                                     'Integer', '[1-9]|[1-9][0-9]|1[0-5][0-9]|16[0-8]', false, 0, NULL, now(), now()),
                                    (uuidv7(), 'Identity:Registration:CompletionUrl', 'http://localhost:3000/cadastro/concluir',
                                     'Endereço da tela que conclui o cadastro de cliente, usado no link enviado por e-mail (#20). O sistema acrescenta ?token=<token> ao final, então o valor não pode ter query string nem fragmento. Formato: URL http ou https sem espaços, ? ou #. Alterar este valor vale para os próximos e-mails.',
                                     'String', 'https?://[^\s?#]+', false, 0, NULL, now(), now());

                                INSERT INTO identity.parameter_history
                                    (external_id, parameter_key, revision, previous_value, new_value, previous_description, new_description,
                                     data_type, validation_pattern, is_sensitive, changed_by, changed_at, created_at, updated_at)
                                SELECT uuidv7(), key, 0, NULL, value, NULL, description,
                                       data_type, validation_pattern, is_sensitive, NULL, now(), now(), now()
                                FROM identity.parameters
                                WHERE key IN ('Identity:Registration:TokenLifetimeHours', 'Identity:Registration:CompletionUrl');
                INSERT INTO identity.parameters
                                    (external_id, key, value, description, data_type, validation_pattern, is_sensitive, revision, updated_by, created_at, updated_at)
                                VALUES
                                    (uuidv7(), 'Identity:PasswordReset:TokenLifetimeMinutes', '60',
                                     'Validade, em minutos, do link de redefinição de senha enviado por e-mail (#22). Depois desse prazo o link não redefine a senha (#23) e a pessoa precisa pedir de novo. Formato: inteiro de 1 a 1440. Alterar este valor vale para os próximos links; os já enviados mantêm a validade com que foram gerados.',
                                     'Integer', '[1-9][0-9]{0,2}|1[0-3][0-9]{2}|14[0-3][0-9]|1440', false, 0, NULL, now(), now()),
                                    (uuidv7(), 'Identity:PasswordReset:CompletionUrl', 'http://localhost:3000/senha/redefinir',
                                     'Endereço da tela que redefine a senha, usado no link enviado por e-mail (#22). O sistema acrescenta ?token=<token> ao final, então o valor não pode ter query string nem fragmento. Formato: URL http ou https sem espaços, ? ou #. Alterar este valor vale para os próximos e-mails.',
                                     'String', 'https?://[^\s?#]+', false, 0, NULL, now(), now());

                                INSERT INTO identity.parameter_history
                                    (external_id, parameter_key, revision, previous_value, new_value, previous_description, new_description,
                                     data_type, validation_pattern, is_sensitive, changed_by, changed_at, created_at, updated_at)
                                SELECT uuidv7(), key, 0, NULL, value, NULL, description,
                                       data_type, validation_pattern, is_sensitive, NULL, now(), now(), now()
                                FROM identity.parameters
                                WHERE key IN ('Identity:PasswordReset:TokenLifetimeMinutes', 'Identity:PasswordReset:CompletionUrl');
                    ELSE
                        IF to_regclass('settings.parameters') IS NULL OR to_regclass('settings.parameter_history') IS NULL THEN
                            RAISE EXCEPTION 'Identity parameter migration: incomplete source' USING ERRCODE = '23514';
                        END IF;
                        LOCK TABLE settings.parameters, settings.parameter_history IN SHARE MODE;
                        IF EXISTS (
                            SELECT 1 FROM settings.parameters p
                            WHERE p.revision < 0 OR btrim(p.key) = '' OR btrim(p.description) = ''
                                OR NOT EXISTS (SELECT 1 FROM settings.parameter_history h
                                    WHERE h.parameter_key = p.key AND h.revision = p.revision
                                        AND h.new_value = p.value AND h.new_description = p.description
                                        AND h.data_type = p.data_type AND h.validation_pattern IS NOT DISTINCT FROM p.validation_pattern
                                        AND h.is_sensitive = p.is_sensitive AND h.changed_by IS NOT DISTINCT FROM p.updated_by)
                        ) THEN
                            RAISE EXCEPTION 'Identity parameter migration: invalid source history' USING ERRCODE = '23514';
                        END IF;
                        INSERT INTO identity.parameters
                            (id, key, value, description, data_type, validation_pattern, is_sensitive, revision, updated_by,
                             external_id, created_at, updated_at)
                        OVERRIDING SYSTEM VALUE
                        SELECT id, key, value, description, data_type, validation_pattern, is_sensitive, revision, updated_by,
                               external_id, created_at, updated_at FROM settings.parameters;
                        INSERT INTO identity.parameter_history
                            (id, parameter_key, revision, previous_value, new_value, previous_description, new_description,
                             data_type, validation_pattern, is_sensitive, changed_by, changed_at, external_id, created_at, updated_at)
                        OVERRIDING SYSTEM VALUE
                        SELECT id, parameter_key, revision, previous_value, new_value, previous_description, new_description,
                               data_type, validation_pattern, is_sensitive, changed_by, changed_at, external_id, created_at, updated_at
                        FROM settings.parameter_history;
                        IF EXISTS (SELECT to_jsonb(p) FROM settings.parameters p EXCEPT ALL SELECT to_jsonb(p) FROM identity.parameters p)
                            OR EXISTS (SELECT to_jsonb(p) FROM identity.parameters p EXCEPT ALL SELECT to_jsonb(p) FROM settings.parameters p)
                            OR EXISTS (SELECT to_jsonb(h) FROM settings.parameter_history h EXCEPT ALL SELECT to_jsonb(h) FROM identity.parameter_history h)
                            OR EXISTS (SELECT to_jsonb(h) FROM identity.parameter_history h EXCEPT ALL SELECT to_jsonb(h) FROM settings.parameter_history h)
                        THEN
                            RAISE EXCEPTION 'Identity parameter migration: copy validation failed' USING ERRCODE = '23514';
                        END IF;
                    END IF;
                END $migration$;
                SELECT setval(pg_get_serial_sequence('identity.parameters', 'id'), COALESCE(MAX(id), 1), MAX(id) IS NOT NULL)
                FROM identity.parameters;
                SELECT setval(pg_get_serial_sequence('identity.parameter_history', 'id'), COALESCE(MAX(id), 1), MAX(id) IS NOT NULL)
                FROM identity.parameter_history;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Depois de novas escritas, voltar à origem exige reconciliação; nunca apagar dados automaticamente.
            throw new NotSupportedException("A reversão de parâmetros exige backup e reconciliação manual das novas escritas de Identity.");
        }
    }
}
