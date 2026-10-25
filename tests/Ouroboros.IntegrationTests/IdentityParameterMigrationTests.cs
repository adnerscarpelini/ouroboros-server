using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ouroboros.Modules.Identity.Contracts;
using Ouroboros.Modules.Identity.Infrastructure.Persistence;
using Ouroboros.Modules.Settings.Infrastructure.Persistence;
using LegacyPersistence = Ouroboros.Modules.Settings.Infrastructure.Persistence.ParameterPersistence;
using LegacyWriter = Ouroboros.Modules.Settings.Application.Parameters.ParameterWriter;

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
            Assert.Equal(before, await SnapshotAsync(source, "settings"));
            Assert.False(context.Database.HasPendingModelChanges());
            Assert.Equal("migrated.example", await scope.ServiceProvider.GetRequiredService<ISystemParameters>().GetAsync(Key, Cancellation));
            // Sequências avançam além dos ids copiados, sem colidir na próxima escrita.
            await scope.ServiceProvider.GetRequiredService<Ouroboros.Modules.Identity.Application.Parameters.ParameterWriter>()
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
            Assert.Equal(8, await context.Parameters.CountAsync(Cancellation));
            Assert.Equal(8, await context.ParameterHistory.CountAsync(Cancellation));
            Assert.Equal("adnerscarpelini.com", await scope.ServiceProvider.GetRequiredService<ISystemParameters>().GetAsync(Key, Cancellation));
            Assert.All(await context.Parameters.ToListAsync(Cancellation), parameter =>
                Assert.Null(Ouroboros.Modules.Identity.Domain.Parameters.ParameterValueRules.Validate(parameter.DataType, parameter.ValidationPattern, parameter.Value)));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await postgres.DropDatabaseAsync(database);
        }
    }

    private async Task PrepareLegacyAsync(string database)
    {
        await using var factory = new IntegrationApiFactory(postgres.ConnectionStringFor(database));
        await using var scope = factory.Services.CreateAsyncScope();
        var legacy = scope.ServiceProvider.GetRequiredService<SettingsDbContext>();
        await legacy.Database.MigrateAsync(Cancellation);
        await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().GetService<IMigrator>()
            .MigrateAsync(PreviousMigration, Cancellation);
        var writer = new LegacyWriter(new LegacyPersistence(legacy), TimeProvider.System);
        await writer.ChangeAsync(Key, "migrated.example", "Descrição alterada antes da transferência.", Guid.CreateVersion7(), Cancellation);
    }

    private Task<string?> SnapshotAsync(string database, string schema) => ScalarAsync(database, $"""
        SELECT jsonb_build_object(
            'parameters', (SELECT jsonb_agg(to_jsonb(p) ORDER BY p.key) FROM {schema}.parameters p),
            'history', (SELECT jsonb_agg(to_jsonb(h) ORDER BY h.parameter_key, h.revision) FROM {schema}.parameter_history h))::text
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
