using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ouroboros.Identity.Application.Security;
using Ouroboros.Identity.Domain.Users;
using Ouroboros.Identity.Infrastructure.Persistence;

namespace Ouroboros.IntegrationTests;

// Regressões do QA da transferência: sequências nunca usadas, reconciliação idempotente e com janela preservada, cópia
// repetível, sondas do cutover, estrutura no digest e permissões dos backups.
public sealed partial class IdentityDatabaseTransferTests
{
    [Fact]
    public async Task Reconcile_advances_sequences_that_were_never_used_in_the_origin()
    {
        await using var scenario = await ScenarioAsync(seed: false);
        var transfer = await scenario.CutOverAsync();
        await using (var api = scenario.TargetApi())
        {
            await using (var scope = api.Services.CreateAsyncScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
                context.Users.Add(User.Create("nova@example.com", scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(NewPassword), UserRole.Customer));
                await context.SaveChangesAsync(Cancellation);
            }

            using var client = api.CreateClient();
            Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, "nova@example.com", NewPassword)).Status);
        }

        NpgsqlConnection.ClearAllPools();
        Succeeded(await instance.TransferAsync(scenario.Source, "rollback", transfer.Id));
        Succeeded(await instance.TransferAsync(scenario.Source, "reconcile", transfer.Id));

        // De volta à origem, o próximo cadastro, a próxima sessão e o próximo token não repetem ids já usados no destino.
        await using var origin = scenario.SourceApi();
        await using (var scope = origin.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            context.Users.Add(User.Create("outra@example.com", scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(NewPassword), UserRole.Customer));
            await context.SaveChangesAsync(Cancellation);
        }

        using var originClient = origin.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(originClient, "outra@example.com", NewPassword)).Status);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(originClient, "nova@example.com", NewPassword)).Status);
    }

    [Fact]
    public async Task Reconcile_is_idempotent_and_a_conflict_keeps_the_write_free_window()
    {
        await using var scenario = await ScenarioAsync();
        var transfer = await scenario.CutOverAsync();
        await instance.ExecuteAdminAsync(ProvisionedInstance.IdentityDatabase, "UPDATE identity.users SET failed_login_count = 1 WHERE email = '" + scenario.CustomerEmail + "'");
        Succeeded(await instance.TransferAsync(scenario.Source, "rollback", transfer.Id));
        Succeeded(await instance.TransferAsync(scenario.Source, "reconcile", transfer.Id));

        var again = await instance.TransferAsync(scenario.Source, "reconcile", transfer.Id);
        Succeeded(again);
        Assert.Contains("já foram aplicadas", again.Stdout, StringComparison.Ordinal);

        // Conflito em outra rodada: a origem continua na janela sem escrita depois da recusa.
        await instance.ExecuteAdminAsync(ProvisionedInstance.IdentityDatabase, "UPDATE identity.users SET failed_login_count = 2 WHERE email = '" + scenario.CustomerEmail + "'");
        Succeeded(await instance.TransferAsync(scenario.Source, "rollback", transfer.Id));
        Succeeded(await instance.TransferAsync(scenario.Source, "freeze"));
        await using (var connection = instance.ConnectAdmin(scenario.Source))
        {
            await connection.OpenAsync(Cancellation);
            await WriteIgnoringTheWindowAsync(connection, "UPDATE identity.users SET failed_login_count = 9");
        }

        var conflict = await instance.TransferAsync(scenario.Source, "reconcile", transfer.Id);
        Assert.NotEqual(0, conflict.ExitCode);
        Assert.Equal(1L, await CountAsync("postgres", $"SELECT count(*) FROM pg_db_role_setting s JOIN pg_database d ON d.oid = s.setdatabase WHERE d.datname = '{scenario.Source}' AND s.setconfig::text LIKE '%default_transaction_read_only=on%'"));
    }

    [Fact]
    public async Task Copy_validates_passwords_before_changing_anything_and_can_be_repeated()
    {
        await using var scenario = await ScenarioAsync();
        Succeeded(await instance.TransferAsync(scenario.Source, "freeze"));
        var id = IdOf(await instance.TransferAsync(scenario.Source, "backup"));

        var same = new Dictionary<string, string> { ["IDENTITY_MIGRATION_PASSWORD"] = instance.Passwords[ProvisionedInstance.IdentityRuntime] };
        var refused = await instance.TransferAsync(scenario.Source, "copy", id, same);

        Assert.NotEqual(0, refused.ExitCode);
        Assert.Equal(0L, await CountAsync(ProvisionedInstance.IdentityDatabase, "SELECT count(*) FROM pg_namespace WHERE nspname = 'identity'"));
        Succeeded(await instance.TransferAsync(scenario.Source, "copy", id));
    }

    [Fact]
    public async Task Cutover_detects_runtime_privileges_that_provisioning_would_deny()
    {
        await using var scenario = await ScenarioAsync();
        var transfer = await scenario.CopyAsync();

        await instance.ExecuteAdminAsync(ProvisionedInstance.IdentityDatabase, "GRANT CREATE ON SCHEMA public TO identity_runtime");
        var publicSchema = await instance.TransferAsync(scenario.Source, "cutover", transfer.Id);
        await instance.ExecuteAdminAsync(ProvisionedInstance.IdentityDatabase, "REVOKE CREATE ON SCHEMA public FROM identity_runtime");

        await instance.ExecuteAdminAsync("postgres", "GRANT CONNECT ON DATABASE ouroboros_notifications TO identity_runtime");
        var otherService = await instance.TransferAsync(scenario.Source, "cutover", transfer.Id);
        await instance.ExecuteAdminAsync("postgres", "REVOKE CONNECT ON DATABASE ouroboros_notifications FROM identity_runtime");

        Assert.NotEqual(0, publicSchema.ExitCode);
        Assert.Contains("schema public", publicSchema.Stderr, StringComparison.Ordinal);
        Assert.NotEqual(0, otherService.ExitCode);
        Assert.Contains("Notifications", otherService.Stderr, StringComparison.Ordinal);
        Succeeded(await instance.TransferAsync(scenario.Source, "cutover", transfer.Id));
    }

    [Fact]
    public async Task Validation_detects_structural_tampering_and_the_backup_directory_is_private()
    {
        await using var scenario = await ScenarioAsync();
        var transfer = await scenario.CopyAsync();

        var directory = await instance.ExecAsync("stat", "-c", "%a", $"/tmp/transfers/identity-{transfer.Id}");
        var dump = await instance.ExecAsync("stat", "-c", "%a", $"/tmp/transfers/identity-{transfer.Id}/source.dump");
        Assert.Equal("700", directory.Stdout.Trim());
        Assert.Equal("600", dump.Stdout.Trim());

        foreach (var tampering in new[]
        {
            "ALTER TABLE identity.users ALTER COLUMN failed_login_count DROP DEFAULT",
            "COMMENT ON TABLE identity.users IS 'adulterado'",
            "ALTER TABLE identity.users ALTER COLUMN email TYPE varchar(100)",
        })
        {
            await instance.ExecuteAdminAsync(ProvisionedInstance.IdentityDatabase, tampering);
            var validation = await instance.TransferAsync(scenario.Source, "validate", transfer.Id);
            Assert.NotEqual(0, validation.ExitCode);
            Assert.Contains("structure.", validation.Stdout, StringComparison.Ordinal);

            // Desfaz a adulteração para a próxima: copia de novo o destino a partir do backup.
            await instance.ExecuteAdminAsync(ProvisionedInstance.IdentityDatabase, "DROP SCHEMA identity CASCADE");
            Succeeded(await instance.TransferAsync(scenario.Source, "copy", transfer.Id));
        }
    }

    [Fact]
    public async Task Help_lists_the_commands()
    {
        var help = await instance.TransferAsync("ouroboros", "help");

        Succeeded(help);
        Assert.Contains("cutover", help.Stdout, StringComparison.Ordinal);
        Assert.Contains("reconcile", help.Stdout, StringComparison.Ordinal);
    }
}
