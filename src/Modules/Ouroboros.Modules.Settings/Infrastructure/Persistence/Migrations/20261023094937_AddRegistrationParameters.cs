using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ouroboros.Modules.Settings.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRegistrationParameters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Parâmetros da solicitação de cadastro de cliente (#20), com a revisão 0 registrada no histórico.
            migrationBuilder.Sql("""
                INSERT INTO settings.parameters
                    (external_id, key, value, description, data_type, validation_pattern, is_sensitive, revision, updated_by, created_at, updated_at)
                VALUES
                    (uuidv7(), 'Identity:Registration:TokenLifetimeHours', '24',
                     'Validade, em horas, do link de cadastro de cliente enviado por e-mail (#20). Depois desse prazo o link não conclui o cadastro (#21) e a pessoa precisa solicitar de novo. Formato: inteiro de 1 a 168. Alterar este valor vale para os próximos links; os já enviados mantêm a validade com que foram gerados.',
                     'Integer', '[1-9]|[1-9][0-9]|1[0-5][0-9]|16[0-8]', false, 0, NULL, now(), now()),
                    (uuidv7(), 'Identity:Registration:CompletionUrl', 'http://localhost:3000/cadastro/concluir',
                     'Endereço da tela que conclui o cadastro de cliente, usado no link enviado por e-mail (#20). O sistema acrescenta ?token=<token> ao final, então o valor não pode ter query string nem fragmento. Formato: URL http ou https sem espaços, ? ou #. Alterar este valor vale para os próximos e-mails.',
                     'String', 'https?://[^\s?#]+', false, 0, NULL, now(), now());

                INSERT INTO settings.parameter_history
                    (external_id, parameter_key, revision, previous_value, new_value, previous_description, new_description,
                     data_type, validation_pattern, is_sensitive, changed_by, changed_at, created_at, updated_at)
                SELECT uuidv7(), key, 0, NULL, value, NULL, description,
                       data_type, validation_pattern, is_sensitive, NULL, now(), now(), now()
                FROM settings.parameters
                WHERE key IN ('Identity:Registration:TokenLifetimeHours', 'Identity:Registration:CompletionUrl');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM settings.parameter_history
                WHERE parameter_key IN ('Identity:Registration:TokenLifetimeHours', 'Identity:Registration:CompletionUrl');

                DELETE FROM settings.parameters
                WHERE key IN ('Identity:Registration:TokenLifetimeHours', 'Identity:Registration:CompletionUrl');
                """);
        }
    }
}
