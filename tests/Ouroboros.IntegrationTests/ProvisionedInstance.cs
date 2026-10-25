using System.Security.Cryptography;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Ouroboros.IntegrationTests;

// Uma instância PostgreSQL própria, como a do Compose: banco original com dados legados (volume
// "existente") e o provisionamento de scripts/database executado pelo mesmo psql do container.
// Fica separada da PostgresFixture porque cria roles e revoga CONNECT, efeitos de instância inteira.
public sealed class ProvisionedInstance : IAsyncLifetime
{
    public const string LegacyDatabase = "ouroboros";
    public const string IdentityDatabase = "ouroboros_identity";
    public const string NotificationsDatabase = "ouroboros_notifications";
    public const string IdentityRuntime = "identity_runtime";
    public const string IdentityMigrator = "identity_migrator";
    public const string NotificationsRuntime = "notifications_runtime";
    public const string NotificationsMigrator = "notifications_migrator";

    // A instância registra todas as instruções e durações: o provisionamento precisa neutralizar isso
    // nas próprias sessões, senão as senhas apareceriam no log do servidor.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine")
        .WithCommand("-c", "log_statement=all", "-c", "log_min_duration_statement=0", "-c", "log_min_error_statement=debug1")
        .Build();

    // Senhas só de teste, geradas a cada execução; nunca aparecem em saída nem em logs.
    public IReadOnlyDictionary<string, string> Passwords { get; } = new Dictionary<string, string>
    {
        [IdentityRuntime] = Secret(),
        [IdentityMigrator] = Secret(),
        [NotificationsRuntime] = Secret(),
        [NotificationsMigrator] = Secret(),
    };

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        await ExecuteAdminAsync("postgres", $"""CREATE DATABASE "{LegacyDatabase}" """);
        await ExecuteAdminAsync(LegacyDatabase, "CREATE TABLE legacy_users (id int PRIMARY KEY, email text NOT NULL); INSERT INTO legacy_users VALUES (1, 'a@example.com'), (2, 'b@example.com');");

        var first = await ProvisionAsync();
        Assert.True(first.ExitCode == 0, first.Stderr);
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    public async Task<ScriptResult> ProvisionAsync(IReadOnlyDictionary<string, string>? environment = null)
    {
        await _container.CopyAsync(RepositoryRoot.Resolve("scripts", "database"), "/scripts");
        var variables = environment ?? new Dictionary<string, string>
        {
            ["IDENTITY_RUNTIME_PASSWORD"] = Passwords[IdentityRuntime],
            ["IDENTITY_MIGRATION_PASSWORD"] = Passwords[IdentityMigrator],
            ["NOTIFICATIONS_RUNTIME_PASSWORD"] = Passwords[NotificationsRuntime],
            ["NOTIFICATIONS_MIGRATION_PASSWORD"] = Passwords[NotificationsMigrator],
        };
        var command = new List<string> { "env", $"POSTGRES_DB={LegacyDatabase}" };
        command.AddRange(variables.Select(pair => $"{pair.Key}={pair.Value}"));
        command.AddRange(["psql", "--username", "postgres", "--dbname", "postgres", "--no-psqlrc", "--set=ON_ERROR_STOP=1", "--file=/scripts/provision.sql"]);
        var result = await _container.ExecAsync(command);
        return new ScriptResult(result.ExitCode ?? -1, result.Stdout, result.Stderr);
    }

    // Executa scripts/database/transfer-identity.sh no container, com a credencial administrativa da instância.
    public async Task<ScriptResult> TransferAsync(string source, string command, string? id = null, IReadOnlyDictionary<string, string>? extra = null)
    {
        await _container.CopyAsync(RepositoryRoot.Resolve("scripts", "database"), "/scripts");
        var variables = new Dictionary<string, string>
        {
            ["SOURCE_DATABASE"] = source,
            ["POSTGRES_DB"] = LegacyDatabase,
            ["TRANSFER_DIR"] = "/tmp/transfers",
            ["IDENTITY_RUNTIME_PASSWORD"] = Passwords[IdentityRuntime],
            ["IDENTITY_MIGRATION_PASSWORD"] = Passwords[IdentityMigrator],
            ["NOTIFICATIONS_RUNTIME_PASSWORD"] = Passwords[NotificationsRuntime],
            ["NOTIFICATIONS_MIGRATION_PASSWORD"] = Passwords[NotificationsMigrator],
            ["PGUSER"] = "postgres",
            ["PGDATABASE"] = "postgres",
        };
        foreach (var (key, value) in extra ?? new Dictionary<string, string>())
        {
            variables[key] = value;
        }

        var arguments = new List<string> { "env" };
        arguments.AddRange(variables.Select(pair => $"{pair.Key}={pair.Value}"));
        arguments.AddRange(["bash", "/scripts/transfer-identity.sh", command]);
        if (id is not null)
        {
            arguments.Add(id);
        }

        var result = await _container.ExecAsync(arguments);
        return new ScriptResult(result.ExitCode ?? -1, result.Stdout, result.Stderr);
    }

    // Comando qualquer no container (por exemplo, test -f), para conferir arquivos preservados.
    public async Task<ScriptResult> ExecAsync(params string[] command)
    {
        var result = await _container.ExecAsync(command);
        return new ScriptResult(result.ExitCode ?? -1, result.Stdout, result.Stderr);
    }

    // O mesmo resumo que o procedimento compara, para provar que a origem ficou intacta.
    public async Task<string> DigestAsync(string database)
    {
        await _container.CopyAsync(RepositoryRoot.Resolve("scripts", "database"), "/scripts");
        var result = await ExecAsync("psql", "-X", "-q", "-U", "postgres", "-d", database, "-f", "/scripts/identity-digest.sql");
        Assert.Equal(0, result.ExitCode);
        return result.Stdout;
    }

    public string ConnectionStringFor(string role, string database)
    {
        var builder = new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = database };
        if (role != "postgres")
        {
            builder.Username = role;
            builder.Password = Passwords[role];
        }

        return builder.ConnectionString;
    }

    public async Task<string> LogsAsync()
    {
        var (stdout, stderr) = await _container.GetLogsAsync();
        return stdout + stderr;
    }

    // Conexão com uma role de serviço, sem pool para que cada tentativa passe pela autorização.
    public NpgsqlConnection Connect(string role, string database)
    {
        var builder = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Username = role,
            Password = Passwords[role],
            Database = database,
            Pooling = false,
        };
        return new NpgsqlConnection(builder.ConnectionString);
    }

    public NpgsqlConnection ConnectAdmin(string database)
    {
        var builder = new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = database, Pooling = false };
        return new NpgsqlConnection(builder.ConnectionString);
    }

    public async Task ExecuteAdminAsync(string database, string sql)
    {
        await using var connection = ConnectAdmin(database);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static string Secret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));

    public sealed record ScriptResult(long ExitCode, string Stdout, string Stderr);
}
