using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ouroboros.Modules.Settings.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPasswordResetParameters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Parâmetros da solicitação de redefinição de senha (#22), com a revisão 0 registrada no histórico.
            migrationBuilder.Sql("""
                INSERT INTO settings.parameters
                    (external_id, key, value, description, data_type, validation_pattern, is_sensitive, revision, updated_by, created_at, updated_at)
                VALUES
                    (uuidv7(), 'Identity:PasswordReset:TokenLifetimeMinutes', '60',
                     'Validade, em minutos, do link de redefinição de senha enviado por e-mail (#22). Depois desse prazo o link não redefine a senha (#23) e a pessoa precisa pedir de novo. Formato: inteiro de 1 a 1440. Alterar este valor vale para os próximos links; os já enviados mantêm a validade com que foram gerados.',
                     'Integer', '[1-9][0-9]{0,2}|1[0-3][0-9]{2}|14[0-3][0-9]|1440', false, 0, NULL, now(), now()),
                    (uuidv7(), 'Identity:PasswordReset:CompletionUrl', 'http://localhost:3000/senha/redefinir',
                     'Endereço da tela que redefine a senha, usado no link enviado por e-mail (#22). O sistema acrescenta ?token=<token> ao final, então o valor não pode ter query string nem fragmento. Formato: URL http ou https sem espaços, ? ou #. Alterar este valor vale para os próximos e-mails.',
                     'String', 'https?://[^\s?#]+', false, 0, NULL, now(), now());

                INSERT INTO settings.parameter_history
                    (external_id, parameter_key, revision, previous_value, new_value, previous_description, new_description,
                     data_type, validation_pattern, is_sensitive, changed_by, changed_at, created_at, updated_at)
                SELECT uuidv7(), key, 0, NULL, value, NULL, description,
                       data_type, validation_pattern, is_sensitive, NULL, now(), now(), now()
                FROM settings.parameters
                WHERE key IN ('Identity:PasswordReset:TokenLifetimeMinutes', 'Identity:PasswordReset:CompletionUrl');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM settings.parameter_history
                WHERE parameter_key IN ('Identity:PasswordReset:TokenLifetimeMinutes', 'Identity:PasswordReset:CompletionUrl');

                DELETE FROM settings.parameters
                WHERE key IN ('Identity:PasswordReset:TokenLifetimeMinutes', 'Identity:PasswordReset:CompletionUrl');
                """);
        }
    }
}
