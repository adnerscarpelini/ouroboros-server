extern alias identityapi;

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ouroboros.Identity.Application.Security;
using Ouroboros.Identity.Domain.Users;
using Ouroboros.Identity.Infrastructure.Persistence;
using Ouroboros.Notifications;

namespace Ouroboros.IntegrationTests;

public sealed class OriginalDatabaseDecommissioningTests(ProvisionedInstance instance) : IClassFixture<ProvisionedInstance>
{
    private const string CustomerEmail = "cliente-decommission@example.com";
    private const string AdminEmail = "admin-decommission@adnerscarpelini.com";
    private const string Password = "uma frase longa e segura para desativacao do banco original";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("postgres")]
    [InlineData("template0")]
    [InlineData("template1")]
    [InlineData(ProvisionedInstance.IdentityDatabase)]
    [InlineData(ProvisionedInstance.NotificationsDatabase)]
    public async Task Maintenance_and_service_databases_cannot_be_decommissioned(string database)
    {
        var result = await instance.DecommissionAsync(database, "final-backup");
        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task Backup_package_cannot_be_used_for_a_different_source_or_an_invalid_path()
    {
        var source = await CreateCutoverOriginWithSettingsSchemaAsync();
        try
        {
            var backup = await instance.DecommissionAsync(source, "final-backup");
            Assert.True(backup.ExitCode == 0, backup.Stdout + backup.Stderr);
            var packageId = ExtractPackageId(backup.Stdout);
            var wrongSource = await instance.DecommissionAsync(ProvisionedInstance.LegacyDatabase, "reactivate", packageId);
            Assert.NotEqual(0, wrongSource.ExitCode);
            Assert.Contains("não pertence", wrongSource.Stderr, StringComparison.Ordinal);

            var invalidPath = await instance.DecommissionAsync(source, "reactivate", "../other-package");
            Assert.NotEqual(0, invalidPath.ExitCode);
            Assert.Contains("id do pacote inválido", invalidPath.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            await CleanupSourceDatabaseAsync(source);
        }
    }

    [Theory]
    [InlineData(ProvisionedInstance.IdentityRuntime)]
    [InlineData(ProvisionedInstance.IdentityMigrator)]
    [InlineData(ProvisionedInstance.NotificationsRuntime)]
    [InlineData(ProvisionedInstance.NotificationsMigrator)]
    public async Task Credential_rotation_rejects_service_roles(string role)
    {
        var result = await instance.DecommissionAsync(
            ProvisionedInstance.LegacyDatabase,
            "rotate-admin-credential",
            extra: new Dictionary<string, string>
            {
                ["ADMIN_ROLE"] = role,
                ["NEW_ADMIN_PASSWORD"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)),
            });
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("role ativa de serviço", result.Stderr, StringComparison.Ordinal);
        await using var connection = instance.Connect(role,
            role.StartsWith("identity", StringComparison.Ordinal) ? ProvisionedInstance.IdentityDatabase : ProvisionedInstance.NotificationsDatabase);
        await connection.OpenAsync(Cancellation);
    }

    [Fact]
    public async Task Inventory_and_final_backup_capture_transferred_identity_and_retained_settings_schemas_and_verify_restore()
    {
        var source = await CreateCutoverOriginWithSettingsSchemaAsync();
        try
        {
            var inventory = await instance.DecommissionAsync(source, "inventory");
            Assert.True(inventory.ExitCode == 0, inventory.Stdout + inventory.Stderr);

            // Inventário identifica os schemas remanescentes (identity, public, settings),
            // as tabelas transferidas para ouroboros_identity e os objetos retidos apenas na origem (settings e public).
            Assert.Contains("schemas|identity,public,settings", inventory.Stdout, StringComparison.Ordinal);
            Assert.Contains("table|identity|users|transferred_to_target|rows=2", inventory.Stdout, StringComparison.Ordinal);
            Assert.Contains("table|identity|__EFMigrationsHistory|migration_history_transferred|", inventory.Stdout, StringComparison.Ordinal);
            Assert.Contains("table|settings|parameters|retained_only_in_source|rows=8", inventory.Stdout, StringComparison.Ordinal);
            Assert.Contains("table|settings|parameter_history|retained_only_in_source|rows=8", inventory.Stdout, StringComparison.Ordinal);
            Assert.Contains("table|settings|__EFMigrationsHistory|migration_history_retired_in_source|rows=1|migrations=20260322014949_InitialSettings", inventory.Stdout, StringComparison.Ordinal);
            Assert.Contains("table|public|legacy_users|retained_only_in_source|rows=2", inventory.Stdout, StringComparison.Ordinal);

            var backup = await instance.DecommissionAsync(source, "final-backup");
            Assert.True(backup.ExitCode == 0, backup.Stdout + backup.Stderr);
            Assert.Contains("Backup final verificado:", backup.Stdout, StringComparison.Ordinal);

            var packageId = ExtractPackageId(backup.Stdout);
            Assert.Equal(0, (await instance.ExecAsync("test", "-s", $"/tmp/decommission/original-{packageId}/final.dump")).ExitCode);
            Assert.Equal(0, (await instance.ExecAsync("test", "-s", $"/tmp/decommission/original-{packageId}/final.dump.sha256")).ExitCode);
            Assert.Equal(0, (await instance.ExecAsync("test", "-s", $"/tmp/decommission/original-{packageId}/inventory.txt")).ExitCode);
            Assert.Equal(0, (await instance.ExecAsync("test", "-s", $"/tmp/decommission/original-{packageId}/final-backup-verified")).ExitCode);

            // O banco temporário de verificação do backup é removido após a comparação.
            await using var admin = instance.ConnectAdmin("postgres");
            await admin.OpenAsync(Cancellation);
            await using var checkCmd = new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE datname LIKE 'original_decommission_check_%'", admin);
            Assert.Equal(0L, (long)(await checkCmd.ExecuteScalarAsync(Cancellation))!);
        }
        finally
        {
            await CleanupSourceDatabaseAsync(source);
        }
    }

    [Fact]
    public async Task Deactivation_requires_explicit_approval_verified_backup_and_zero_runtime_usage_and_supports_reactivation()
    {
        var source = await CreateCutoverOriginWithSettingsSchemaAsync();
        try
        {
            var backup = await instance.DecommissionAsync(source, "final-backup");
            Assert.True(backup.ExitCode == 0, backup.Stdout + backup.Stderr);
            var packageId = ExtractPackageId(backup.Stdout);

            // 1. Sem aprovação explícita do dono do projeto (CONFIRM_ORIGINAL_DB_DEACTIVATION), recusa desativar.
            var withoutApproval = await instance.DecommissionAsync(
                source,
                "deactivate",
                packageId,
                new Dictionary<string, string> { ["RETENTION_DAYS"] = "30" });
            Assert.NotEqual(0, withoutApproval.ExitCode);
            Assert.Contains("CONFIRM_ORIGINAL_DB_DEACTIVATION=DEACTIVATE:" + source, withoutApproval.Stderr, StringComparison.Ordinal);

            // 2. Sem período de retenção válido (RETENTION_DAYS), recusa desativar.
            var withoutRetention = await instance.DecommissionAsync(
                source,
                "deactivate",
                packageId,
                new Dictionary<string, string>
                {
                    ["CONFIRM_ORIGINAL_DB_DEACTIVATION"] = $"DEACTIVATE:{source}",
                    ["RETENTION_DAYS"] = "0",
                });
            Assert.NotEqual(0, withoutRetention.ExitCode);
            Assert.Contains("RETENTION_DAYS", withoutRetention.Stderr, StringComparison.Ordinal);

            // 3. Se houver sessão ativa conectada ao banco original, verify-unused / deactivate recusam.
            await using (var activeSession = instance.ConnectAdmin(source))
            {
                await activeSession.OpenAsync(Cancellation);
                var withActiveSession = await instance.DecommissionAsync(
                    source,
                    "deactivate",
                    packageId,
                    new Dictionary<string, string>
                    {
                        ["CONFIRM_ORIGINAL_DB_DEACTIVATION"] = $"DEACTIVATE:{source}",
                        ["RETENTION_DAYS"] = "30",
                    });
                Assert.NotEqual(0, withActiveSession.ExitCode);
                Assert.Contains("sessão(ões) ativa(s)", withActiveSession.Stderr, StringComparison.Ordinal);
            }

            // 4. Se uma role de serviço ainda tiver CONNECT no banco original, verify-unused / deactivate recusam.
            await instance.ExecuteAdminAsync("postgres", $"GRANT CONNECT ON DATABASE \"{source}\" TO identity_runtime");
            try
            {
                var withServicePrivilege = await instance.DecommissionAsync(
                    source,
                    "deactivate",
                    packageId,
                    new Dictionary<string, string>
                    {
                        ["CONFIRM_ORIGINAL_DB_DEACTIVATION"] = $"DEACTIVATE:{source}",
                        ["RETENTION_DAYS"] = "30",
                    });
                Assert.NotEqual(0, withServicePrivilege.ExitCode);
                Assert.Contains("identity_runtime", withServicePrivilege.Stderr, StringComparison.Ordinal);
            }
            finally
            {
                await instance.ExecuteAdminAsync("postgres", $"REVOKE CONNECT ON DATABASE \"{source}\" FROM identity_runtime");
            }

            // 5. Com aprovação explícita, retenção definida, backup verificado e zero uso por runtime, desativa sem excluir.
            var deactivated = await instance.DecommissionAsync(
                source,
                "deactivate",
                packageId,
                new Dictionary<string, string>
                {
                    ["CONFIRM_ORIGINAL_DB_DEACTIVATION"] = $"DEACTIVATE:{source}",
                    ["RETENTION_DAYS"] = "30",
                });
            Assert.True(deactivated.ExitCode == 0, deactivated.Stdout + deactivated.Stderr);
            Assert.Contains("Nenhum dado, banco ou volume foi excluído", deactivated.Stdout, StringComparison.Ordinal);
            Assert.Equal(0, (await instance.ExecAsync("test", "-s", $"/tmp/decommission/original-{packageId}/deactivated.manifest")).ExitCode);

            // O banco original continua existindo no catálogo, mas não aceita conexões (datallowconn = false, datconnlimit = 0).
            await using (var admin = instance.ConnectAdmin("postgres"))
            {
                await admin.OpenAsync(Cancellation);
                await using var checkCmd = new NpgsqlCommand($"SELECT datallowconn, datconnlimit FROM pg_database WHERE datname = '{source}'", admin);
                await using var reader = await checkCmd.ExecuteReaderAsync(Cancellation);
                Assert.True(await reader.ReadAsync(Cancellation));
                Assert.False(reader.GetBoolean(0));
                Assert.Equal(0, reader.GetInt32(1));
            }

            await using (var blockedConnection = instance.ConnectAdmin(source))
            {
                var error = await Assert.ThrowsAsync<PostgresException>(() => blockedConnection.OpenAsync(Cancellation));
                Assert.Equal("55000", error.SqlState);
            }

            // Identity continua operando normalmente em ouroboros_identity com identity_runtime.
            await using (var targetApi = CreateTargetIdentityApi())
            using (var client = targetApi.CreateClient())
            {
                using var login = await client.PostAsJsonAsync("/api/v1/identity/sessions", new { Email = CustomerEmail, Password }, Cancellation);
                Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            }

            // 6. Procedimento verificável de recuperação (reactivate): reabilita o banco original em somente leitura com todos os dados preservados.
            var reactivated = await instance.DecommissionAsync(source, "reactivate", packageId);
            Assert.True(reactivated.ExitCode == 0, reactivated.Stdout + reactivated.Stderr);

            await using (var reopened = instance.ConnectAdmin(source))
            {
                await reopened.OpenAsync(Cancellation);
                await using var countSettings = new NpgsqlCommand("SELECT count(*) FROM settings.parameters", reopened);
                Assert.Equal(8L, (long)(await countSettings.ExecuteScalarAsync(Cancellation))!);
                await using var countUsers = new NpgsqlCommand("SELECT count(*) FROM identity.users", reopened);
                Assert.Equal(2L, (long)(await countUsers.ExecuteScalarAsync(Cancellation))!);
            }
        }
        finally
        {
            await CleanupSourceDatabaseAsync(source);
        }
    }

    [Fact]
    public async Task Rotating_or_revoking_legacy_credential_after_rollback_window_keeps_services_working_and_does_not_leak_passwords_to_logs()
    {
        var source = await CreateCutoverOriginWithSettingsSchemaAsync();
        const string legacyRole = "legacy_host_broad_role";
        var legacyPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var rotatedAdminPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));

        var notificationsMigratorCs = instance.ConnectionStringFor(ProvisionedInstance.NotificationsMigrator, ProvisionedInstance.NotificationsDatabase);
        await Ouroboros.Notifications.DependencyInjection.MigrateDatabaseAsync(notificationsMigratorCs, Cancellation);
        Assert.Equal(0, (await instance.ProvisionAsync()).ExitCode);

        try
        {
            // Simula uma credencial antiga ampla usada antes do cutover.
            await instance.ExecuteAdminAsync(
                "postgres",
                $"DROP ROLE IF EXISTS {legacyRole}; CREATE ROLE {legacyRole} LOGIN SUPERUSER CREATEDB CREATEROLE PASSWORD '{legacyPassword}';");

            var rotateResult = await instance.DecommissionAsync(
                source,
                "rotate-admin-credential",
                extra: new Dictionary<string, string>
                {
                    ["LEGACY_ROLE"] = legacyRole,
                    ["ADMIN_ROLE"] = "postgres",
                    ["NEW_ADMIN_PASSWORD"] = rotatedAdminPassword,
                });
            Assert.True(rotateResult.ExitCode == 0, rotateResult.Stdout + rotateResult.Stderr);
            Assert.Contains($"Credencial legada '{legacyRole}' revogada", rotateResult.Stdout, StringComparison.Ordinal);
            Assert.Contains("Credencial administrativa 'postgres' rotacionada com sucesso sem afetar os serviços.", rotateResult.Stdout, StringComparison.Ordinal);

            // 1. A credencial antiga revogada não consegue mais autenticar nem possui privilégios amplos.
            var legacyBuilder = new NpgsqlConnectionStringBuilder(instance.ConnectionStringFor("postgres", source))
            {
                Username = legacyRole,
                Password = legacyPassword,
                Pooling = false,
            };
            await using (var legacyConn = new NpgsqlConnection(legacyBuilder.ConnectionString))
            {
                await Assert.ThrowsAsync<PostgresException>(() => legacyConn.OpenAsync(Cancellation));
            }

            // 2. Identity e Notifications continuam funcionando normalmente com suas roles de runtime próprias.
            await using (var targetApi = CreateTargetIdentityApi())
            using (var client = targetApi.CreateClient())
            {
                using var login = await client.PostAsJsonAsync("/api/v1/identity/sessions", new { Email = CustomerEmail, Password }, Cancellation);
                Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            }

            await using (var notifConn = instance.Connect(ProvisionedInstance.NotificationsRuntime, ProvisionedInstance.NotificationsDatabase))
            {
                await notifConn.OpenAsync(Cancellation);
                await using var cmd = new NpgsqlCommand("SELECT count(*) FROM notifications.inbox", notifConn);
                Assert.True((long)(await cmd.ExecuteScalarAsync(Cancellation))! >= 0);
            }

            // 3. A nova senha administrativa não aparece em stdout, stderr nem nos logs verbosos do PostgreSQL.
            Assert.DoesNotContain(rotatedAdminPassword, rotateResult.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain(rotatedAdminPassword, rotateResult.Stderr, StringComparison.Ordinal);
            var serverLogs = await instance.LogsAsync();
            Assert.DoesNotContain(rotatedAdminPassword, serverLogs, StringComparison.Ordinal);
        }
        finally
        {
            // Restaura a senha original do container de teste (postgres) e limpa a role de teste.
            var originalContainerAdminPassword = new NpgsqlConnectionStringBuilder(instance.ConnectionStringFor("postgres", "postgres")).Password!;
            await instance.DecommissionAsync(
                source,
                "rotate-admin-credential",
                extra: new Dictionary<string, string>
                {
                    ["ADMIN_ROLE"] = "postgres",
                    ["NEW_ADMIN_PASSWORD"] = originalContainerAdminPassword,
                });
            await instance.ExecuteAdminAsync("postgres", $"DROP ROLE IF EXISTS {legacyRole};");
            await CleanupSourceDatabaseAsync(source);
        }
    }

    private async Task<string> CreateCutoverOriginWithSettingsSchemaAsync()
    {
        var source = "ouroboros_orig_" + Guid.NewGuid().ToString("N")[..10];
        await instance.ExecuteAdminAsync("postgres", $"CREATE DATABASE \"{source}\"");
        await instance.ExecuteAdminAsync("postgres", $"REVOKE CONNECT, TEMPORARY ON DATABASE \"{source}\" FROM PUBLIC");

        await using (var sourceApi = new IntegrationApiFactory(instance.ConnectionStringFor("postgres", source)))
        await using (var scope = sourceApi.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            await db.Database.MigrateAsync(Cancellation);
            var hash = scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(Password);
            db.Users.Add(User.Create(CustomerEmail, hash, UserRole.Customer));
            db.Users.Add(User.Create(AdminEmail, hash, UserRole.Admin));
            await db.SaveChangesAsync(Cancellation);
        }

        // Prepara os objetos que permaneceram no banco original após a migração (#81, #82, #96):
        // 1) Tabela legada no schema public;
        // 2) Schema settings com parameters, parameter_history e __EFMigrationsHistory retidos na origem.
        await instance.ExecuteAdminAsync(
            source,
            """
            CREATE TABLE public.legacy_users (id int PRIMARY KEY, email text NOT NULL);
            INSERT INTO public.legacy_users VALUES (1, 'legacy1@example.com'), (2, 'legacy2@example.com');

            CREATE SCHEMA settings;
            CREATE TABLE settings."__EFMigrationsHistory" (
                "MigrationId" character varying(150) NOT NULL PRIMARY KEY,
                "ProductVersion" character varying(32) NOT NULL
            );
            INSERT INTO settings."__EFMigrationsHistory" VALUES ('20260322014949_InitialSettings', '10.0.5');

            CREATE TABLE settings.parameters (LIKE identity.parameters INCLUDING ALL);
            INSERT INTO settings.parameters OVERRIDING SYSTEM VALUE SELECT * FROM identity.parameters;

            CREATE TABLE settings.parameter_history (LIKE identity.parameter_history INCLUDING ALL);
            INSERT INTO settings.parameter_history OVERRIDING SYSTEM VALUE SELECT * FROM identity.parameter_history;
            """);

        NpgsqlConnection.ClearAllPools();

        // Executa a transferência oficial (#82) para ouroboros_identity.
        await instance.ExecuteAdminAsync(ProvisionedInstance.IdentityDatabase, "DROP SCHEMA IF EXISTS identity CASCADE");
        Assert.Equal(0, (await instance.TransferAsync(source, "freeze")).ExitCode);
        var backup = await instance.TransferAsync(source, "backup");
        Assert.Equal(0, backup.ExitCode);
        var transferId = Regex.Match(backup.Stdout, @"Transferência:\s*(\S+)").Groups[1].Value;
        Assert.Equal(0, (await instance.TransferAsync(source, "copy", transferId)).ExitCode);
        Assert.Equal(0, (await instance.TransferAsync(source, "cutover", transferId)).ExitCode);

        return source;
    }

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<identityapi::Ouroboros.Identity.Api.IdentityApiEntryPoint> CreateTargetIdentityApi() =>
        new IntegrationApiFactory(instance.ConnectionStringFor(ProvisionedInstance.IdentityRuntime, ProvisionedInstance.IdentityDatabase))
            .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Identity"] = instance.ConnectionStringFor(ProvisionedInstance.IdentityRuntime, ProvisionedInstance.IdentityDatabase),
                })));

    private async Task CleanupSourceDatabaseAsync(string source)
    {
        NpgsqlConnection.ClearAllPools();
        await instance.ExecuteAdminAsync("postgres", $"ALTER DATABASE \"{source}\" WITH ALLOW_CONNECTIONS true CONNECTION LIMIT -1");
        await instance.ExecuteAdminAsync("postgres", $"DROP DATABASE IF EXISTS \"{source}\" WITH (FORCE)");
    }

    private static string ExtractPackageId(string stdout)
    {
        var match = Regex.Match(stdout, @"Pacote:\s*(\S+)");
        Assert.True(match.Success, $"Id do pacote não encontrado na saída:\n{stdout}");
        return match.Groups[1].Value;
    }
}
