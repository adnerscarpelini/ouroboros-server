using System.Data;
using Npgsql;

namespace Ouroboros.IntegrationTests;

// Provisionamento de bancos e roles (scripts/database) em PostgreSQL real: idempotência sobre um
// volume que já tem dados, isolamento entre serviços e privilégios mínimos de runtime e migration.
public sealed class DatabaseProvisioningTests(ProvisionedInstance instance) : IClassFixture<ProvisionedInstance>
{
    private const string CreateItems =
        "CREATE SCHEMA IF NOT EXISTS identity; CREATE TABLE IF NOT EXISTS identity.items (id int GENERATED ALWAYS AS IDENTITY PRIMARY KEY, name text NOT NULL)";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Rerunning_creates_nothing_twice_and_preserves_existing_data()
    {
        await using var admin = instance.ConnectAdmin("postgres");
        var before = await CatalogAsync(admin);
        const string LegacyRows = "SELECT string_agg(id || email, ',' ORDER BY id) FROM legacy_users";
        await using var legacy = instance.ConnectAdmin(ProvisionedInstance.LegacyDatabase);
        var legacyBefore = await ScalarAsync<string>(legacy, LegacyRows);

        // Dados do serviço criados pela role de migration sobrevivem a novas execuções.
        await using (var migrator = instance.Connect(ProvisionedInstance.IdentityMigrator, ProvisionedInstance.IdentityDatabase))
        {
            await ExecuteAsync(migrator, "CREATE SCHEMA IF NOT EXISTS identity; CREATE TABLE IF NOT EXISTS identity.keep (id int PRIMARY KEY); INSERT INTO identity.keep VALUES (1) ON CONFLICT DO NOTHING");
        }

        var second = await instance.ProvisionAsync();
        var third = await instance.ProvisionAsync();

        Assert.Equal(0, second.ExitCode);
        Assert.Equal(0, third.ExitCode);
        Assert.Equal(before, await CatalogAsync(admin));
        Assert.Equal(legacyBefore, await ScalarAsync<string>(legacy, LegacyRows));
        await using var identity = instance.ConnectAdmin(ProvisionedInstance.IdentityDatabase);
        Assert.Equal(1L, await ScalarAsync<long>(identity, "SELECT count(*) FROM identity.keep"));
        Assert.Equal(1L, await ScalarAsync<long>(admin, "SELECT count(*) FROM pg_database WHERE datname = 'ouroboros_identity'"));
        Assert.Equal(1L, await ScalarAsync<long>(admin, "SELECT count(*) FROM pg_roles WHERE rolname = 'identity_runtime'"));
    }

    [Fact]
    public async Task Service_roles_have_no_instance_privileges()
    {
        await using var admin = instance.ConnectAdmin("postgres");

        var roles = await ScalarAsync<long>(admin, """
            SELECT count(*) FROM pg_roles
            WHERE rolname IN ('identity_runtime','identity_migrator','notifications_runtime','notifications_migrator')
              AND rolcanlogin AND NOT (rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls)
            """);

        Assert.Equal(4L, roles);
    }

    [Theory]
    [InlineData(ProvisionedInstance.IdentityRuntime, ProvisionedInstance.NotificationsDatabase)]
    [InlineData(ProvisionedInstance.IdentityRuntime, ProvisionedInstance.LegacyDatabase)]
    [InlineData(ProvisionedInstance.IdentityRuntime, "postgres")]
    [InlineData(ProvisionedInstance.IdentityMigrator, ProvisionedInstance.NotificationsDatabase)]
    [InlineData(ProvisionedInstance.IdentityMigrator, ProvisionedInstance.LegacyDatabase)]
    [InlineData(ProvisionedInstance.NotificationsRuntime, ProvisionedInstance.IdentityDatabase)]
    [InlineData(ProvisionedInstance.NotificationsRuntime, ProvisionedInstance.LegacyDatabase)]
    [InlineData(ProvisionedInstance.NotificationsMigrator, ProvisionedInstance.IdentityDatabase)]
    public async Task Service_role_cannot_connect_to_another_database(string role, string database)
    {
        await using var connection = instance.Connect(role, database);

        var error = await Assert.ThrowsAsync<PostgresException>(() => connection.OpenAsync(Cancellation));

        Assert.Equal("42501", error.SqlState);
    }

    [Theory]
    [InlineData(ProvisionedInstance.IdentityRuntime, ProvisionedInstance.IdentityDatabase)]
    [InlineData(ProvisionedInstance.IdentityMigrator, ProvisionedInstance.IdentityDatabase)]
    [InlineData(ProvisionedInstance.NotificationsRuntime, ProvisionedInstance.NotificationsDatabase)]
    [InlineData(ProvisionedInstance.NotificationsMigrator, ProvisionedInstance.NotificationsDatabase)]
    public async Task Service_role_connects_to_its_own_database(string role, string database)
    {
        await using var connection = instance.Connect(role, database);

        Assert.Equal(role, await ScalarAsync<string>(connection, "SELECT current_user"));
    }

    [Fact]
    public async Task Runtime_role_reads_and_writes_what_the_migration_role_created_but_not_the_migration_history()
    {
        await using (var migrator = instance.Connect(ProvisionedInstance.IdentityMigrator, ProvisionedInstance.IdentityDatabase))
        {
            await ExecuteAsync(migrator, CreateItems);
            await ExecuteAsync(migrator, "CREATE TABLE IF NOT EXISTS identity.\"__EFMigrationsHistory\" (id text PRIMARY KEY)");
        }

        // O acesso ao histórico converge na reexecução, depois das migrations.
        Assert.Equal(0, (await instance.ProvisionAsync()).ExitCode);

        await using var runtime = instance.Connect(ProvisionedInstance.IdentityRuntime, ProvisionedInstance.IdentityDatabase);
        await ExecuteAsync(runtime, "INSERT INTO identity.items (name) VALUES ('a')");
        await ExecuteAsync(runtime, "UPDATE identity.items SET name = 'b' WHERE name = 'a'");
        Assert.True(await ScalarAsync<long>(runtime, "SELECT count(*) FROM identity.items") >= 1);
        await ExecuteAsync(runtime, "DELETE FROM identity.items WHERE name = 'b'");

        await AssertDeniedAsync(runtime, "SELECT count(*) FROM identity.\"__EFMigrationsHistory\"");
    }

    [Theory]
    [InlineData("CREATE TABLE identity.intruder (id int)")]
    [InlineData("CREATE TABLE public.intruder (id int)")]
    [InlineData("CREATE SCHEMA intruder")]
    [InlineData("CREATE TEMPORARY TABLE intruder (id int)")]
    [InlineData("ALTER TABLE identity.items ADD COLUMN extra int")]
    [InlineData("DROP TABLE identity.items")]
    [InlineData("TRUNCATE identity.items")]
    [InlineData("CREATE INDEX ix_intruder ON identity.items (name)")]
    [InlineData("CREATE DATABASE intruder")]
    [InlineData("CREATE ROLE intruder LOGIN")]
    [InlineData("ALTER ROLE identity_runtime SUPERUSER")]
    public async Task Runtime_role_is_denied_ddl_and_instance_administration(string statement)
    {
        await using (var migrator = instance.Connect(ProvisionedInstance.IdentityMigrator, ProvisionedInstance.IdentityDatabase))
        {
            await ExecuteAsync(migrator, CreateItems);
        }

        await using var runtime = instance.Connect(ProvisionedInstance.IdentityRuntime, ProvisionedInstance.IdentityDatabase);

        await AssertDeniedAsync(runtime, statement);
    }

    [Fact]
    public async Task Migration_role_runs_ddl_only_in_its_own_database()
    {
        await using var migrator = instance.Connect(ProvisionedInstance.NotificationsMigrator, ProvisionedInstance.NotificationsDatabase);

        await ExecuteAsync(migrator, "CREATE SCHEMA notifications; CREATE TABLE notifications.t (id int); ALTER TABLE notifications.t ADD COLUMN c int; DROP TABLE notifications.t; DROP SCHEMA notifications");

        await AssertDeniedAsync(migrator, "CREATE DATABASE intruder");
        await AssertDeniedAsync(migrator, "CREATE ROLE intruder LOGIN");
        await AssertDeniedAsync(migrator, "ALTER ROLE notifications_migrator SUPERUSER");
    }

    [Fact]
    public async Task Missing_or_repeated_passwords_fail_before_creating_anything()
    {
        await using var admin = instance.ConnectAdmin("postgres");
        var before = await ScalarAsync<long>(admin, "SELECT count(*) FROM pg_roles");
        var repeated = new Dictionary<string, string>
        {
            ["IDENTITY_RUNTIME_PASSWORD"] = "mesma-senha",
            ["IDENTITY_MIGRATION_PASSWORD"] = "mesma-senha",
        };

        var missing = await instance.ProvisionAsync(new Dictionary<string, string>());
        var same = await instance.ProvisionAsync(repeated);

        Assert.NotEqual(0, missing.ExitCode);
        Assert.NotEqual(0, same.ExitCode);
        Assert.Equal(before, await ScalarAsync<long>(admin, "SELECT count(*) FROM pg_roles"));
        Assert.DoesNotContain("mesma-senha", same.Stdout + same.Stderr + await instance.LogsAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Passwords_never_appear_in_script_output_or_server_logs()
    {
        var result = await instance.ProvisionAsync();
        var logs = await instance.LogsAsync();
        var output = result.Stdout + result.Stderr + logs;

        Assert.Equal(0, result.ExitCode);
        // Garante que o log estava realmente verboso: a ausência das senhas vem da neutralização.
        Assert.Contains("statement:", logs, StringComparison.Ordinal);
        Assert.Contains("CREATE DATABASE", logs, StringComparison.Ordinal);
        foreach (var password in instance.Passwords.Values)
        {
            Assert.DoesNotContain(password, output, StringComparison.Ordinal);
        }
    }

    private static async Task AssertDeniedAsync(NpgsqlConnection connection, string statement)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, statement));
        Assert.Equal("42501", error.SqlState);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await EnsureOpenAsync(connection);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Cancellation);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await EnsureOpenAsync(connection);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Cancellation))!;
    }

    private static async Task EnsureOpenAsync(NpgsqlConnection connection)
    {
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(Cancellation);
        }
    }

    // Identidade estável de bancos e roles: o mesmo oid prova que nada foi recriado.
    private static Task<string> CatalogAsync(NpgsqlConnection admin) => ScalarAsync<string>(admin, """
        SELECT (SELECT string_agg(oid || ':' || datname || ':' || datdba, ',' ORDER BY datname) FROM pg_database)
            || '|' || (SELECT string_agg(oid || ':' || rolname, ',' ORDER BY rolname) FROM pg_roles WHERE rolname !~ '^pg_')
        """);
}
