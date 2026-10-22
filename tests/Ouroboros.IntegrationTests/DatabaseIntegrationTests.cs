using System.Net.Http.Json;

namespace Ouroboros.IntegrationTests;

public sealed class DatabaseIntegrationTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    [Fact]
    public async Task Api_runs_against_migrated_database_and_answers_request()
    {
        var response = await Client.GetAsync("/api/v1/integration-probe/migrations", TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var applied = await response.Content.ReadFromJsonAsync<Dictionary<string, string[]>>(TestContext.Current.CancellationToken);
        Assert.NotNull(applied);
        Assert.Contains(applied["IdentityDbContext"], migration => migration.EndsWith("_InitialCreate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Identity_schema_and_migrations_history_exist()
    {
        var historyTable = await QueryScalarAsync<string>(
            "SELECT table_name::text FROM information_schema.tables " +
            "WHERE table_schema = 'identity' AND table_name = '__EFMigrationsHistory'");

        Assert.Equal("__EFMigrationsHistory", historyTable);
    }

    // Cada caso cria a mesma tabela. Se os testes compartilhassem o banco, o segundo CREATE TABLE
    // falharia, independentemente da ordem de execução.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Each_test_gets_its_own_database(int _)
    {
        await ExecuteAsync("CREATE TABLE public.isolation_marker (id int)");
        await ExecuteAsync("INSERT INTO public.isolation_marker VALUES (1)");

        var rows = await QueryScalarAsync<long>("SELECT count(*) FROM public.isolation_marker");

        Assert.Equal(1, rows);
    }
}
