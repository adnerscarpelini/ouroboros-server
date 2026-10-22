using Npgsql;

namespace Ouroboros.IntegrationTests;

// Base dos testes de integração: cada teste tem seu próprio banco, clonado do modelo já migrado, e
// sua própria instância da API. O banco é removido ao final.
public abstract class IntegrationTest(PostgresFixture postgres) : IAsyncLifetime
{
    private string _database = "";
    private IntegrationApiFactory? _factory;

    protected string ConnectionString { get; private set; } = "";

    protected IntegrationApiFactory Api => _factory ?? throw new InvalidOperationException("Teste não inicializado.");

    protected HttpClient Client { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        _database = await postgres.CreateDatabaseAsync();
        ConnectionString = postgres.ConnectionStringFor(_database);
        _factory = new IntegrationApiFactory(ConnectionString);
        Client = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        // Se a criação do banco falhou, não há o que remover; evita esconder o erro original.
        if (_database.Length > 0)
        {
            NpgsqlConnection.ClearAllPools();
            await postgres.DropDatabaseAsync(_database);
        }

        GC.SuppressFinalize(this);
    }

    protected async Task<T?> QueryScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        var result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return result is null or DBNull ? default : (T)result;
    }

    protected async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
