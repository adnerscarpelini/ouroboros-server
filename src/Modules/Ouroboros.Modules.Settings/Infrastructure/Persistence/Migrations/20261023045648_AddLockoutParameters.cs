using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ouroboros.Modules.Settings.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLockoutParameters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Parâmetros do bloqueio de conta (#13), com a revisão 0 registrada no histórico.
            migrationBuilder.Sql("""
                INSERT INTO settings.parameters
                    (external_id, key, value, description, data_type, validation_pattern, is_sensitive, revision, updated_by, created_at, updated_at)
                VALUES
                    (uuidv7(), 'Identity:Lockout:MaxFailedAttempts', '5',
                     'Quantidade de falhas de senha seguidas que bloqueia a conta no login (#13). Ao atingir o limite, a conta fica bloqueada pelo tempo de Identity:Lockout:DurationMinutes e o contador volta a zero; um login bem-sucedido antes do limite também zera o contador. Formato: inteiro de 1 a 99. Alterar este valor vale a partir da próxima falha de senha e não libera contas já bloqueadas.',
                     'Integer', '[1-9][0-9]?', false, 0, NULL, now(), now()),
                    (uuidv7(), 'Identity:Lockout:DurationMinutes', '15',
                     'Duração, em minutos, do bloqueio da conta após Identity:Lockout:MaxFailedAttempts falhas de senha seguidas (#13). Durante o bloqueio o login responde o mesmo 401 de senha errada, mesmo com a senha correta. O bloqueio expira sozinho. Formato: inteiro de 1 a 1440. Alterar este valor vale para os próximos bloqueios e não muda o fim dos bloqueios em andamento.',
                     'Integer', '[1-9][0-9]{0,2}|1[0-3][0-9]{2}|14[0-3][0-9]|1440', false, 0, NULL, now(), now());

                INSERT INTO settings.parameter_history
                    (external_id, parameter_key, revision, previous_value, new_value, previous_description, new_description,
                     data_type, validation_pattern, is_sensitive, changed_by, changed_at, created_at, updated_at)
                SELECT uuidv7(), key, 0, NULL, value, NULL, description,
                       data_type, validation_pattern, is_sensitive, NULL, now(), now(), now()
                FROM settings.parameters
                WHERE key IN ('Identity:Lockout:MaxFailedAttempts', 'Identity:Lockout:DurationMinutes');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM settings.parameter_history
                WHERE parameter_key IN ('Identity:Lockout:MaxFailedAttempts', 'Identity:Lockout:DurationMinutes');

                DELETE FROM settings.parameters
                WHERE key IN ('Identity:Lockout:MaxFailedAttempts', 'Identity:Lockout:DurationMinutes');
                """);
        }
    }
}
