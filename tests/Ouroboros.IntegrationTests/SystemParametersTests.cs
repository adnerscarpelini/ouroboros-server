using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ouroboros.Modules.Settings.Application.Parameters;
using Ouroboros.Modules.Settings.Contracts;
using Ouroboros.Modules.Settings.Domain.Parameters;
using Ouroboros.Modules.Settings.Infrastructure.Persistence;

namespace Ouroboros.IntegrationTests;

// Parâmetros do sistema contra o PostgreSQL real: leitura pelo contrato, alteração com histórico na
// mesma transação e validação na gravação.
public sealed class SystemParametersTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private const string DomainKey = "Identity:AdminInvitations:AllowedEmailDomain";
    private const string ApproversKey = "Identity:AdminInvitations:ApproverEmails";

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
        Assert.Equal(2L, await QueryScalarAsync<long>("SELECT count(*) FROM settings.parameters WHERE is_sensitive AND length(description) > 100"));
        Assert.Equal("Domain", await QueryScalarAsync<string>($"SELECT data_type FROM settings.parameters WHERE key = '{DomainKey}'"));
        Assert.Equal(
            await QueryScalarAsync<long>("SELECT count(*) FROM settings.parameters"),
            await QueryScalarAsync<long>("SELECT count(*) FROM settings.parameter_history WHERE revision = 0 AND previous_value IS NULL"));
    }

    [Fact]
    public async Task Every_seeded_value_satisfies_its_own_rules()
    {
        await using var scope = Api.Services.CreateAsyncScope();
        var seeded = await scope.ServiceProvider.GetRequiredService<SettingsDbContext>().Parameters
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
        var context = check.ServiceProvider.GetRequiredService<SettingsDbContext>();
        var parameter = await context.Parameters.SingleAsync(candidate => candidate.Key == DomainKey, TestContext.Current.CancellationToken);
        var history = await context.ParameterHistory
            .SingleAsync(entry => entry.ParameterKey == DomainKey && entry.Revision == 1, TestContext.Current.CancellationToken);

        Assert.Equal(1, parameter.Revision);
        Assert.Equal(admin, parameter.UpdatedBy);
        Assert.Equal("adnerscarpelini.com", history.PreviousValue);
        Assert.Equal("ouroboros.example", history.NewValue);
        Assert.Equal("Nova descrição detalhada.", history.NewDescription);
        Assert.NotNull(history.PreviousDescription);
        Assert.Equal(admin, history.ChangedBy);
    }

    [Fact]
    public async Task Invalid_value_is_rejected_and_nothing_is_written()
    {
        var historyBefore = await QueryScalarAsync<long>("SELECT count(*) FROM settings.parameter_history");
        await using (var scope = Api.Services.CreateAsyncScope())
        {
            var writer = scope.ServiceProvider.GetRequiredService<ParameterWriter>();
            await Assert.ThrowsAsync<InvalidParameterException>(() => writer.ChangeAsync(
                ApproversKey, "nao-e-email", null, Guid.CreateVersion7(), TestContext.Current.CancellationToken));
        }

        Assert.Equal(historyBefore, await QueryScalarAsync<long>("SELECT count(*) FROM settings.parameter_history"));
        Assert.Equal("oi@adnerscarpelini.com", await QueryScalarAsync<string>($"SELECT value FROM settings.parameters WHERE key = '{ApproversKey}'"));
    }

    // Valor e histórico vão no mesmo SaveChanges: se o histórico não puder ser gravado, o valor também
    // não muda.
    [Fact]
    public async Task Value_and_history_are_written_in_the_same_transaction()
    {
        await ExecuteAsync(
            "INSERT INTO settings.parameter_history (external_id, parameter_key, revision, previous_value, new_value, " +
            "previous_description, new_description, data_type, validation_pattern, is_sensitive, changed_by, changed_at, created_at, updated_at) " +
            $"VALUES (uuidv7(), '{DomainKey}', 1, NULL, 'x', NULL, 'x', 'Domain', NULL, true, NULL, now(), now(), now())");

        await using (var scope = Api.Services.CreateAsyncScope())
        {
            var writer = scope.ServiceProvider.GetRequiredService<ParameterWriter>();
            await Assert.ThrowsAnyAsync<DbUpdateException>(() => writer.ChangeAsync(
                DomainKey, "ouroboros.example", null, Guid.CreateVersion7(), TestContext.Current.CancellationToken));
        }

        Assert.Equal("adnerscarpelini.com", await QueryScalarAsync<string>($"SELECT value FROM settings.parameters WHERE key = '{DomainKey}'"));
        Assert.Equal(0, await QueryScalarAsync<int>($"SELECT revision FROM settings.parameters WHERE key = '{DomainKey}'"));
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
        await using (var scope = Api.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ParameterWriter>().CreateAsync(
                "Teste:Limite", "10", "Limite usado só neste teste.", ParameterDataType.Integer, null, false, null,
                TestContext.Current.CancellationToken);
        }

        Assert.Equal("10", await QueryScalarAsync<string>("SELECT new_value FROM settings.parameter_history WHERE parameter_key = 'Teste:Limite' AND revision = 0"));
    }

    [Fact]
    public async Task Concurrent_changes_to_the_same_parameter_are_detected()
    {
        await using var first = Api.Services.CreateAsyncScope();
        await using var second = Api.Services.CreateAsyncScope();
        var firstContext = first.ServiceProvider.GetRequiredService<SettingsDbContext>();
        var secondContext = second.ServiceProvider.GetRequiredService<SettingsDbContext>();
        var firstCopy = await firstContext.Parameters.SingleAsync(parameter => parameter.Key == DomainKey, TestContext.Current.CancellationToken);
        var secondCopy = await secondContext.Parameters.SingleAsync(parameter => parameter.Key == DomainKey, TestContext.Current.CancellationToken);

        firstContext.ParameterHistory.Add(firstCopy.Change("primeiro.example", null, Guid.CreateVersion7(), DateTimeOffset.UtcNow));
        await firstContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // A segunda gravação falha inteira, seja pela revisão (token de concorrência) ou pelo índice
        // único (parameter_key, revision) do histórico; nenhuma alteração se perde.
        secondContext.ParameterHistory.Add(secondCopy.Change("segundo.example", null, Guid.CreateVersion7(), DateTimeOffset.UtcNow));
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => secondContext.SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Equal("primeiro.example", await QueryScalarAsync<string>($"SELECT value FROM settings.parameters WHERE key = '{DomainKey}'"));
        Assert.Equal(1L, await QueryScalarAsync<long>($"SELECT count(*) FROM settings.parameter_history WHERE parameter_key = '{DomainKey}' AND revision = 1"));
    }
}
