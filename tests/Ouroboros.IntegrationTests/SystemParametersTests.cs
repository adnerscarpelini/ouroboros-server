using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ouroboros.Identity.Application.Parameters;
using Ouroboros.Identity.Contracts;
using Ouroboros.Identity.Domain.Parameters;
using Ouroboros.Identity.Infrastructure.Persistence;

namespace Ouroboros.IntegrationTests;

// Parâmetros do sistema contra o PostgreSQL real: leitura pelo contrato, alteração com histórico na
// mesma transação e validação na gravação.
public sealed class SystemParametersTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private const string DomainKey = "Identity:AdminInvitations:AllowedEmailDomain";
    private const string ApproversKey = "Identity:AdminInvitations:ApproverEmails";

    [Fact]
    public async Task Parameter_history_and_user_changes_share_the_identity_transaction()
    {
        await using var scope = Api.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var sessions = scope.ServiceProvider.GetRequiredService<Ouroboros.Identity.Application.Sessions.ISessionStore>();
        await using var transaction = await sessions.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await scope.ServiceProvider.GetRequiredService<ParameterWriter>().ChangeAsync(
            DomainKey, "atomic.example", null, Guid.CreateVersion7(), TestContext.Current.CancellationToken);
        context.Users.Add(Ouroboros.Identity.Domain.Users.User.Create(
            "parameter-owner@example.com", "test hash", Ouroboros.Identity.Domain.Users.UserRole.Admin));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await transaction.RollbackAsync(TestContext.Current.CancellationToken);

        Assert.Equal("adnerscarpelini.com", await QueryScalarAsync<string>($"SELECT value FROM identity.parameters WHERE key = '{DomainKey}'"));
        Assert.Equal(1L, await QueryScalarAsync<long>($"SELECT count(*) FROM identity.parameter_history WHERE parameter_key = '{DomainKey}'"));
        Assert.Equal(0L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.users"));
    }

    [Fact]
    public async Task Runtime_reads_and_changes_parameters_without_the_settings_schema()
    {
        await ExecuteAsync("ALTER SCHEMA settings RENAME TO retained_settings");
        await using var scope = Api.Services.CreateAsyncScope();
        var reader = scope.ServiceProvider.GetRequiredService<ISystemParameters>();
        Assert.Equal("adnerscarpelini.com", await reader.GetAsync(DomainKey, TestContext.Current.CancellationToken));
        await scope.ServiceProvider.GetRequiredService<ParameterWriter>().ChangeAsync(
            DomainKey, "local.example", null, Guid.CreateVersion7(), TestContext.Current.CancellationToken);
        Assert.Equal("local.example", await reader.GetAsync(DomainKey, TestContext.Current.CancellationToken));
        Assert.Equal("adnerscarpelini.com", await QueryScalarAsync<string>($"SELECT value FROM retained_settings.parameters WHERE key = '{DomainKey}'"));
    }

    [Fact]
    public async Task Seeded_parameters_are_read_through_the_contract()
    {
        await using var scope = Api.Services.CreateAsyncScope();
        var parameters = scope.ServiceProvider.GetRequiredService<ISystemParameters>();

        Assert.Equal("adnerscarpelini.com", await parameters.GetAsync(DomainKey, TestContext.Current.CancellationToken));
        Assert.Equal("oi@adnerscarpelini.com", await parameters.GetAsync(ApproversKey, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Seeded_parameters_have_description_type_and_creation_history()
    {
        Assert.Equal(2L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.parameters WHERE is_sensitive AND length(description) > 100"));
        Assert.Equal("Domain", await QueryScalarAsync<string>($"SELECT data_type FROM identity.parameters WHERE key = '{DomainKey}'"));
        Assert.Equal(
            await QueryScalarAsync<long>("SELECT count(*) FROM identity.parameters"),
            await QueryScalarAsync<long>("SELECT count(*) FROM identity.parameter_history WHERE revision = 0 AND previous_value IS NULL"));
    }

    [Fact]
    public async Task Every_seeded_value_satisfies_its_own_rules()
    {
        await using var scope = Api.Services.CreateAsyncScope();
        var seeded = await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Parameters
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(seeded);
        Assert.All(seeded, parameter => Assert.Null(ParameterValueRules.Validate(parameter.DataType, parameter.ValidationPattern, parameter.Value)));
    }

    [Fact]
    public async Task Change_is_visible_on_next_read_without_restart()
    {
        var admin = Guid.CreateVersion7();
        await using (var scope = Api.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ParameterWriter>()
                .ChangeAsync(DomainKey, "ouroboros.example", null, admin, TestContext.Current.CancellationToken);
        }

        await using var reader = Api.Services.CreateAsyncScope();
        Assert.Equal(
            "ouroboros.example",
            await reader.ServiceProvider.GetRequiredService<ISystemParameters>().GetAsync(DomainKey, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Change_writes_history_with_previous_and_new_values_and_author()
    {
        var admin = Guid.CreateVersion7();
        await using (var scope = Api.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ParameterWriter>()
                .ChangeAsync(DomainKey, "ouroboros.example", "Nova descrição detalhada.", admin, TestContext.Current.CancellationToken);
        }

        await using var check = Api.Services.CreateAsyncScope();
        var context = check.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var parameter = await context.Parameters.SingleAsync(candidate => candidate.Key == DomainKey, TestContext.Current.CancellationToken);
        var history = await context.ParameterHistory
            .SingleAsync(entry => entry.ParameterKey == DomainKey && entry.Revision == 1, TestContext.Current.CancellationToken);

        Assert.Equal(1, parameter.Revision);
        Assert.Equal(admin, parameter.UpdatedBy);
        Assert.Equal(parameter.Value, history.NewValue);
        Assert.Equal(parameter.Description, history.NewDescription);
        Assert.Equal(parameter.DataType, history.DataType);
        Assert.Equal(parameter.ValidationPattern, history.ValidationPattern);
        Assert.Equal(parameter.IsSensitive, history.IsSensitive);
        Assert.Equal("adnerscarpelini.com", history.PreviousValue);
        Assert.Equal("ouroboros.example", history.NewValue);
        Assert.Equal("Nova descrição detalhada.", history.NewDescription);
        Assert.NotNull(history.PreviousDescription);
        Assert.Equal(admin, history.ChangedBy);
    }

    [Fact]
    public async Task Invalid_value_is_rejected_and_nothing_is_written()
    {
        var historyBefore = await QueryScalarAsync<long>("SELECT count(*) FROM identity.parameter_history");
        await using (var scope = Api.Services.CreateAsyncScope())
        {
            var writer = scope.ServiceProvider.GetRequiredService<ParameterWriter>();
            await Assert.ThrowsAsync<InvalidParameterException>(() => writer.ChangeAsync(
                ApproversKey, "nao-e-email", null, Guid.CreateVersion7(), TestContext.Current.CancellationToken));
        }

        Assert.Equal(historyBefore, await QueryScalarAsync<long>("SELECT count(*) FROM identity.parameter_history"));
        Assert.Equal("oi@adnerscarpelini.com", await QueryScalarAsync<string>($"SELECT value FROM identity.parameters WHERE key = '{ApproversKey}'"));
    }

    // Valor e histórico vão no mesmo SaveChanges: se o histórico não puder ser gravado, o valor também
    // não muda.
    [Fact]
    public async Task Value_and_history_are_written_in_the_same_transaction()
    {
        await ExecuteAsync(
            "INSERT INTO identity.parameter_history (external_id, parameter_key, revision, previous_value, new_value, " +
            "previous_description, new_description, data_type, validation_pattern, is_sensitive, changed_by, changed_at, created_at, updated_at) " +
            $"VALUES (uuidv7(), '{DomainKey}', 1, NULL, 'x', NULL, 'x', 'Domain', NULL, true, NULL, now(), now(), now())");

        await using (var scope = Api.Services.CreateAsyncScope())
        {
            var writer = scope.ServiceProvider.GetRequiredService<ParameterWriter>();
            var exception = await Assert.ThrowsAsync<ParameterConflictException>(() => writer.ChangeAsync(
                DomainKey, "ouroboros.example", null, Guid.CreateVersion7(), TestContext.Current.CancellationToken));
            Assert.Equal(DomainKey, exception.Key);
        }

        Assert.Equal("adnerscarpelini.com", await QueryScalarAsync<string>($"SELECT value FROM identity.parameters WHERE key = '{DomainKey}'"));
        Assert.Equal(0, await QueryScalarAsync<int>($"SELECT revision FROM identity.parameters WHERE key = '{DomainKey}'"));
        Assert.Null(await QueryScalarAsync<Guid?>($"SELECT updated_by FROM identity.parameters WHERE key = '{DomainKey}'"));
        Assert.Equal(2L, await QueryScalarAsync<long>($"SELECT count(*) FROM identity.parameter_history WHERE parameter_key = '{DomainKey}'"));
    }

    [Fact]
    public async Task Missing_key_is_an_explicit_error()
    {
        await using var scope = Api.Services.CreateAsyncScope();
        var parameters = scope.ServiceProvider.GetRequiredService<ISystemParameters>();

        var exception = await Assert.ThrowsAsync<ParameterNotFoundException>(
            () => parameters.GetAsync("Nao:Existe", TestContext.Current.CancellationToken));
        Assert.Equal("Nao:Existe", exception.Key);
    }

    [Fact]
    public async Task Creating_a_parameter_writes_creation_history()
    {
        var author = Guid.CreateVersion7();
        await using (var scope = Api.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ParameterWriter>().CreateAsync(
                "Teste:Limite", "10", "Limite usado só neste teste.", ParameterDataType.Integer, "[0-9]+", false, author,
                TestContext.Current.CancellationToken);
        }

        Assert.Equal("10", await QueryScalarAsync<string>("SELECT new_value FROM identity.parameter_history WHERE parameter_key = 'Teste:Limite' AND revision = 0"));
        Assert.Equal(author, await QueryScalarAsync<Guid>("SELECT updated_by FROM identity.parameters WHERE key = 'Teste:Limite'"));
        Assert.Equal(author, await QueryScalarAsync<Guid>("SELECT changed_by FROM identity.parameter_history WHERE parameter_key = 'Teste:Limite'"));
    }

    [Fact]
    public async Task Concurrent_changes_to_the_same_parameter_are_detected()
    {
        await using var first = Api.Services.CreateAsyncScope();
        await using var second = Api.Services.CreateAsyncScope();
        var firstContext = first.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var secondContext = second.ServiceProvider.GetRequiredService<IdentityDbContext>();
        // Ambos os escopos observam a revisão inicial antes da primeira confirmação.
        await firstContext.Parameters.SingleAsync(parameter => parameter.Key == DomainKey, TestContext.Current.CancellationToken);
        await secondContext.Parameters.SingleAsync(parameter => parameter.Key == DomainKey, TestContext.Current.CancellationToken);
        var firstAuthor = Guid.CreateVersion7();

        await first.ServiceProvider.GetRequiredService<ParameterWriter>()
            .ChangeAsync(DomainKey, "primeiro.example", null, firstAuthor, TestContext.Current.CancellationToken);

        // A segunda gravação falha inteira, seja pela revisão (token de concorrência) ou pelo índice
        // único (parameter_key, revision) do histórico; nenhuma alteração se perde.
        await Assert.ThrowsAsync<ParameterConflictException>(() => second.ServiceProvider.GetRequiredService<ParameterWriter>()
            .ChangeAsync(DomainKey, "segundo.example", null, Guid.CreateVersion7(), TestContext.Current.CancellationToken));

        Assert.Equal("primeiro.example", await QueryScalarAsync<string>($"SELECT value FROM identity.parameters WHERE key = '{DomainKey}'"));
        Assert.Equal(1L, await QueryScalarAsync<long>($"SELECT count(*) FROM identity.parameter_history WHERE parameter_key = '{DomainKey}' AND revision = 1"));
        Assert.Equal(firstAuthor, await QueryScalarAsync<Guid>($"SELECT updated_by FROM identity.parameters WHERE key = '{DomainKey}'"));
        Assert.Equal(firstAuthor, await QueryScalarAsync<Guid>($"SELECT changed_by FROM identity.parameter_history WHERE parameter_key = '{DomainKey}' AND revision = 1"));
    }

    [Fact]
    public async Task Missing_key_cannot_be_changed_or_create_history()
    {
        var historyBefore = await QueryScalarAsync<long>("SELECT count(*) FROM identity.parameter_history");
        await using var scope = Api.Services.CreateAsyncScope();
        var exception = await Assert.ThrowsAsync<ParameterNotFoundException>(() =>
            scope.ServiceProvider.GetRequiredService<ParameterWriter>().ChangeAsync(
                "Nao:Existe", "valor", null, Guid.CreateVersion7(), TestContext.Current.CancellationToken));

        Assert.Equal("Nao:Existe", exception.Key);
        Assert.Equal(historyBefore, await QueryScalarAsync<long>("SELECT count(*) FROM identity.parameter_history"));
        Assert.Equal(0L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.parameters WHERE key = 'Nao:Existe'"));
    }

    [Fact]
    public async Task Same_reader_observes_a_change_even_with_a_tracked_old_parameter()
    {
        await using var scope = Api.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Parameters
            .SingleAsync(parameter => parameter.Key == DomainKey, TestContext.Current.CancellationToken);
        var reader = scope.ServiceProvider.GetRequiredService<ISystemParameters>();
        Assert.Equal("adnerscarpelini.com", await reader.GetAsync(DomainKey, TestContext.Current.CancellationToken));

        await using (var writerScope = Api.Services.CreateAsyncScope())
        {
            await writerScope.ServiceProvider.GetRequiredService<ParameterWriter>().ChangeAsync(
                DomainKey, "novo.example", null, Guid.CreateVersion7(), TestContext.Current.CancellationToken);
        }

        Assert.Equal("novo.example", await reader.GetAsync(DomainKey, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Creation_history_failure_rolls_back_the_new_parameter()
    {
        // Reserva a revisão de criação para provocar falha real no índice do histórico.
        await ExecuteAsync("""
            INSERT INTO identity.parameter_history
                (external_id, parameter_key, revision, new_value, new_description, data_type,
                 is_sensitive, changed_at, created_at, updated_at)
            VALUES (uuidv7(), 'Teste:Atomico', 0, 'anterior', 'Anterior.', 'String', false, now(), now(), now())
            """);
        await using var scope = Api.Services.CreateAsyncScope();
        await Assert.ThrowsAsync<ParameterConflictException>(() =>
            scope.ServiceProvider.GetRequiredService<ParameterWriter>().CreateAsync(
                "Teste:Atomico", "novo", "Novo.", ParameterDataType.String, null, false, Guid.CreateVersion7(),
                TestContext.Current.CancellationToken));

        Assert.Equal(0L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.parameters WHERE key = 'Teste:Atomico'"));
        Assert.Equal("anterior", await QueryScalarAsync<string>("SELECT new_value FROM identity.parameter_history WHERE parameter_key = 'Teste:Atomico'"));
    }

    [Fact]
    public async Task Invalid_creation_leaves_no_parameter_or_history()
    {
        await using var scope = Api.Services.CreateAsyncScope();
        await Assert.ThrowsAsync<InvalidParameterException>(() =>
            scope.ServiceProvider.GetRequiredService<ParameterWriter>().CreateAsync(
                "Teste:Invalido", "texto", "Descrição.", ParameterDataType.Integer, null, false, Guid.CreateVersion7(),
                TestContext.Current.CancellationToken));

        Assert.Equal(0L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.parameters WHERE key = 'Teste:Invalido'"));
        Assert.Equal(0L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.parameter_history WHERE parameter_key = 'Teste:Invalido'"));
    }
}
