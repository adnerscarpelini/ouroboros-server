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

        await using (var factory = new IntegrationApiFactory(ConnectionStringFor(TemplateDatabase)))
        {
            using var scope = factory.Services.CreateScope();
            foreach (var context in ModuleDbContexts.Resolve(scope.ServiceProvider))
            {
                await context.Database.MigrateAsync();
            }
        }

        // Nenhuma conexão pode ficar aberta no modelo para que ele seja clonado.
        NpgsqlConnection.ClearAllPools();
    }

    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"ouroboros_test_{Guid.NewGuid():N}";
        await _createLock.WaitAsync();
        try
        {
            await ExecuteAsync($"""CREATE DATABASE "{name}" TEMPLATE "{TemplateDatabase}" """);
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
