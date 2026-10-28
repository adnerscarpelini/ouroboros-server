using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ouroboros.Identity.Contracts;
using Ouroboros.Identity.Infrastructure.Persistence;

namespace Ouroboros.IntegrationTests;

public sealed class IdentityParameterMigrationTests(PostgresFixture postgres)
{
    private const string PreviousMigration = "20261023094930_AddEmailTokens";
    private const string Key = "Identity:AdminInvitations:AllowedEmailDomain";
    private CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migration_preserves_all_fields_and_history_and_backup_is_restorable()
    {
        var source = await postgres.CreateDatabaseAsync(useTemplate: false);
        var restored = await postgres.CreateDatabaseAsync(useTemplate: false);
        try
        {
            await PrepareLegacyAsync(source);
            var before = await SnapshotAsync(source, "settings");
            // A cópia só começa depois de comprovar uma restauração recuperável do backup.
            await postgres.BackupAndRestoreAsync(source, restored);
            Assert.Equal(before, await SnapshotAsync(restored, "settings"));

            await using var factory = new IntegrationApiFactory(postgres.ConnectionStringFor(source));
            await using var scope = factory.Services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            await context.Database.MigrateAsync(Cancellation);

            Assert.Equal(before, await SnapshotAsync(source, "identity"));
            Assert.Equal("24", await scope.ServiceProvider.GetRequiredService<ISystemParameters>().GetAsync("Identity:AdminInvitations:TokenLifetimeHours", Cancellation));
            Assert.Equal("http://localhost:3001/administradores/convite", await scope.ServiceProvider.GetRequiredService<ISystemParameters>().GetAsync("Identity:AdminInvitations:CompletionUrl", Cancellation));
            Assert.Equal(2, await context.ParameterHistory.CountAsync(history => history.ParameterKey == "Identity:AdminInvitations:TokenLifetimeHours"
                || history.ParameterKey == "Identity:AdminInvitations:CompletionUrl", Cancellation));
            Assert.Equal(before, await SnapshotAsync(source, "settings"));
            Assert.False(context.Database.HasPendingModelChanges());
            Assert.Equal("migrated.example", await scope.ServiceProvider.GetRequiredService<ISystemParameters>().GetAsync(Key, Cancellation));
            // Sequências avançam além dos ids copiados, sem colidir na próxima escrita.
            await scope.ServiceProvider.GetRequiredService<Ouroboros.Identity.Application.Parameters.ParameterWriter>()
                .ChangeAsync(Key, "updated.example", "Descrição posterior.", Guid.CreateVersion7(), Cancellation);
            Assert.Equal(before, await SnapshotAsync(source, "settings"));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await postgres.DropDatabaseAsync(source);
            await postgres.DropDatabaseAsync(restored);
        }
    }

    [Fact]
    public async Task Invalid_source_rolls_back_migration_and_never_falls_back_to_settings()
    {
        var database = await postgres.CreateDatabaseAsync(useTemplate: false);
        try
        {
            await PrepareLegacyAsync(database);
            await ExecuteAsync(database, $"UPDATE settings.parameters SET revision = revision + 1 WHERE key = '{Key}'");
            var before = await SnapshotAsync(database, "settings");
            await using var factory = new IntegrationApiFactory(postgres.ConnectionStringFor(database));
            await using var scope = factory.Services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();

            var failure = await Assert.ThrowsAsync<PostgresException>(() => context.Database.MigrateAsync(Cancellation));

            Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
            Assert.Equal(before, await SnapshotAsync(database, "settings"));
            Assert.Null(await ScalarAsync(database, "SELECT to_regclass('identity.parameters')::text"));
            Assert.DoesNotContain(await context.Database.GetAppliedMigrationsAsync(Cancellation), migration => migration.EndsWith("_OwnIdentityParameters", StringComparison.Ordinal));
            var readerFailure = await Assert.ThrowsAsync<PostgresException>(() =>
                scope.ServiceProvider.GetRequiredService<ISystemParameters>().GetAsync(Key, Cancellation));
            Assert.Equal(PostgresErrorCodes.UndefinedTable, readerFailure.SqlState);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await postgres.DropDatabaseAsync(database);
        }
    }

    [Fact]
    public async Task Clean_identity_database_seeds_parameters_without_settings()
    {
        var database = await postgres.CreateDatabaseAsync(useTemplate: false);
        try
        {
            await using var factory = new IntegrationApiFactory(postgres.ConnectionStringFor(database));
            await using var scope = factory.Services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            await context.Database.MigrateAsync(Cancellation);

            Assert.Null(await ScalarAsync(database, "SELECT to_regclass('settings.parameters')::text"));
            Assert.Equal(10, await context.Parameters.CountAsync(Cancellation));
            Assert.Equal(10, await context.ParameterHistory.CountAsync(Cancellation));
            Assert.Equal("adnerscarpelini.com", await scope.ServiceProvider.GetRequiredService<ISystemParameters>().GetAsync(Key, Cancellation));
            Assert.All(await context.Parameters.ToListAsync(Cancellation), parameter =>
                Assert.Null(Ouroboros.Identity.Domain.Parameters.ParameterValueRules.Validate(parameter.DataType, parameter.ValidationPattern, parameter.Value)));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await postgres.DropDatabaseAsync(database);
        }
    }

    // Banco que ainda tem os parâmetros em settings (esquema legado versionado em Resources) e o Identity migrado até a
    // migration anterior à que assume os parâmetros. O valor alterado reproduz a escrita do antigo módulo Settings: nova
    // revisão no parâmetro e uma linha de histórico.
    private async Task PrepareLegacyAsync(string database)
    {
        await ExecuteAsync(database, await File.ReadAllTextAsync(RepositoryRoot.Resolve("tests", "Ouroboros.IntegrationTests", "Resources", "settings-legacy-schema.sql"), Cancellation));

        await using var factory = new IntegrationApiFactory(postgres.ConnectionStringFor(database));
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().GetService<IMigrator>()
            .MigrateAsync(PreviousMigration, Cancellation);

        var author = Guid.CreateVersion7();
        await ExecuteAsync(database, $"""
            INSERT INTO settings.parameter_history
                (parameter_key, revision, previous_value, new_value, previous_description, new_description, data_type,
                 validation_pattern, is_sensitive, changed_by, changed_at, external_id, created_at, updated_at)
            SELECT key, revision + 1, value, 'migrated.example', description, 'Descrição alterada antes da transferência.',
                   data_type, validation_pattern, is_sensitive, '{author}', now(), gen_random_uuid(), now(), now()
            FROM settings.parameters WHERE key = '{Key}';
            UPDATE settings.parameters
            SET value = 'migrated.example', description = 'Descrição alterada antes da transferência.',
                revision = revision + 1, updated_by = '{author}', updated_at = now()
            WHERE key = '{Key}';
            """);
    }

    private Task<string?> SnapshotAsync(string database, string schema) => ScalarAsync(database, $"""
        SELECT jsonb_build_object(
            'parameters', (SELECT jsonb_agg(to_jsonb(p) ORDER BY p.key) FROM {schema}.parameters p
                WHERE p.key IN (SELECT key FROM settings.parameters)),
            'history', (SELECT jsonb_agg(to_jsonb(h) ORDER BY h.parameter_key, h.revision) FROM {schema}.parameter_history h
                WHERE h.parameter_key IN (SELECT key FROM settings.parameters)))::text
        """);

    private async Task<string?> ScalarAsync(string database, string sql)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionStringFor(database));
        await connection.OpenAsync(Cancellation);
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(Cancellation) as string;
    }

    private async Task ExecuteAsync(string database, string sql)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionStringFor(database));
        await connection.OpenAsync(Cancellation);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Cancellation);
    }
}
