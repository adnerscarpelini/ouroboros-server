using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Docker.DotNet;
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
    private PostgreSqlContainer _container = CreateContainer();

    private static PostgreSqlContainer CreateContainer() =>
        new PostgreSqlBuilder("postgres:18-alpine")
            .WithPortBinding(FreeTcpPort(), 5432)
            .WithCommand("-c", "log_statement=all", "-c", "log_min_duration_statement=0", "-c", "log_min_error_statement=debug1",
                "-c", "log_parameter_max_length=0", "-c", "log_parameter_max_length_on_error=0")
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
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await _container.StartAsync();
                break;
            }
            catch (DockerApiException exception) when (attempt < 2 && exception.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
            {
                // A seleção no host e o bind pelo daemon não são atômicos, especialmente no Docker Desktop.
                await _container.DisposeAsync();
                _container = CreateContainer();
            }
        }
        await ExecuteAdminAsync("postgres", $"""CREATE DATABASE "{LegacyDatabase}" """);
        await ExecuteAdminAsync(LegacyDatabase, "CREATE TABLE legacy_users (id int PRIMARY KEY, email text NOT NULL); INSERT INTO legacy_users VALUES (1, 'a@example.com'), (2, 'b@example.com');");

        var first = await ProvisionAsync();
        Assert.True(first.ExitCode == 0, first.Stderr);
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    internal Task StopAsync() => _container.StopAsync();

    internal Task StartAsync() => _container.StartAsync();

    internal async Task<ScriptResult> MessagingRecoveryAsync(string file, string variable, Guid id)
    {
        if (file is not ("resume-notification-processing.sql" or "resume-notification-failure-publication.sql"))
        { throw new ArgumentException("Script de recuperação desconhecido."); }
        await _container.CopyAsync(RepositoryRoot.Resolve("scripts", "messaging"), "/messaging");
        return await ExecAsync("psql", "-X", "-U", "postgres", "-d", NotificationsDatabase,
            "-v", "ON_ERROR_STOP=1", "-v", $"{variable}={id:D}", "-f", "/messaging/" + file);
    }

    internal async Task<string> RestoreProtectedNotificationsBackupAsync()
    {
        const string path = "/tmp/notifications-fixture.dump";
        var backup = await ExecAsync("pg_dump", "-U", "postgres", "-Fc", "--no-owner", "--no-acl", "--file", path, NotificationsDatabase);
        Assert.True(backup.ExitCode == 0, "pg_dump do banco próprio falhou.");
        var restoredBytes = await ProtectedBackupRoundTrip.RunAsync(await _container.ReadFileAsync(path));
        await _container.CopyAsync(restoredBytes, path);
        var name = "notifications_restore_" + Guid.NewGuid().ToString("N");
        await ExecuteAdminAsync("postgres", $"CREATE DATABASE \"{name}\"");
        var restore = await ExecAsync("pg_restore", "-U", "postgres", "--exit-on-error", "--no-owner", "--no-acl", "--dbname", name, path);
        Assert.True(restore.ExitCode == 0, "pg_restore do banco próprio falhou.");
        return name;
    }

    internal Task DropRestoredNotificationsAsync(string database)
    {
        if (!database.StartsWith("notifications_restore_", StringComparison.Ordinal)) { throw new ArgumentException("Exige banco restaurado pela fixture."); }
        return ExecuteAdminAsync("postgres", $"DROP DATABASE \"{database}\" WITH (FORCE)");
    }

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

    // Executa scripts/database/decommission-original-db.sh no container, com a credencial administrativa da instância.
    public async Task<ScriptResult> DecommissionAsync(string source, string command, string? id = null, IReadOnlyDictionary<string, string>? extra = null)
    {
        await _container.CopyAsync(RepositoryRoot.Resolve("scripts", "database"), "/scripts");
        var variables = new Dictionary<string, string>
        {
            ["SOURCE_DATABASE"] = source,
            ["POSTGRES_DB"] = LegacyDatabase,
            ["DECOMMISSION_DIR"] = "/tmp/decommission",
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
        arguments.AddRange(["bash", "/scripts/decommission-original-db.sh", command]);
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

    private static int FreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public sealed record ScriptResult(long ExitCode, string Stdout, string Stderr);
}
