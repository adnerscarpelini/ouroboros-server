using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ouroboros.IntegrationTests;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(PostgresFixture))]

namespace Ouroboros.IntegrationTests;

// Um container PostgreSQL por execução da suíte. As migrations dos módulos são aplicadas uma vez num
// banco modelo; cada teste recebe um banco próprio clonado dele (CREATE DATABASE ... TEMPLATE), o que
// isola o estado sem reaplicar migrations.
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string TemplateDatabase = "ouroboros_template";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine").Build();

    // CREATE DATABASE falha se outra sessão estiver usando o modelo; as criações são serializadas.
    private readonly SemaphoreSlim _createLock = new(1, 1);

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        await ExecuteAsync($"""CREATE DATABASE "{TemplateDatabase}" """);

        await using (var factory = new LegacyExternalHostFactory(ConnectionStringFor(TemplateDatabase)))
        {
            using var scope = factory.Services.CreateScope();
            foreach (var context in ModuleDbContexts.Resolve(scope.ServiceProvider).OrderBy(context => context.GetType().Name == "SettingsDbContext" ? 0 : 1))
            {
                await context.Database.MigrateAsync();
            }
        }

        // Nenhuma conexão pode ficar aberta no modelo para que ele seja clonado.
        NpgsqlConnection.ClearAllPools();
    }

    public async Task<string> CreateDatabaseAsync(bool useTemplate = true)
    {
        var name = $"ouroboros_test_{Guid.NewGuid():N}";
        await _createLock.WaitAsync();
        try
        {
            var template = useTemplate ? TemplateDatabase : "template0";
            await ExecuteAsync($"""CREATE DATABASE "{name}" TEMPLATE "{template}" """);
        }
        finally
        {
            _createLock.Release();
        }

        return name;
    }

    public Task DropDatabaseAsync(string name) =>
        ExecuteAsync($"""DROP DATABASE IF EXISTS "{name}" WITH (FORCE)""");

    public string ConnectionStringFor(string database) =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = database }.ConnectionString;

    // Backup e restauração reais, somente dos bancos isolados que a fixture criou.
    public async Task BackupAndRestoreAsync(string source, string destination)
    {
        if (!source.StartsWith("ouroboros_test_", StringComparison.Ordinal)
            || !destination.StartsWith("ouroboros_test_", StringComparison.Ordinal))
        {
            throw new ArgumentException("Backup de teste exige bancos da fixture.");
        }

        var path = $"/tmp/{Guid.NewGuid():N}.dump";
        var username = new NpgsqlConnectionStringBuilder(_container.GetConnectionString()).Username!;
        var backup = await _container.ExecAsync(["pg_dump", "--username", username, "--format=custom", "--no-owner", "--no-acl", "--file", path, source]);
        Assert.Equal(0, backup.ExitCode);
        var restore = await _container.ExecAsync(["pg_restore", "--username", username, "--exit-on-error", "--no-owner", "--no-acl", "--dbname", destination, path]);
        Assert.Equal(0, restore.ExitCode);
    }

    public async ValueTask DisposeAsync()
    {
        _createLock.Dispose();
        await _container.DisposeAsync();
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
