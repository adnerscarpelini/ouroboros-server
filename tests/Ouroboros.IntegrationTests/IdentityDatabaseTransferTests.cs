extern alias identityapi;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ouroboros.Identity.Application.Parameters;
using Ouroboros.Identity.Application.Security;
using Ouroboros.Identity.Domain.Users;
using Ouroboros.Identity.Infrastructure.Persistence;

namespace Ouroboros.IntegrationTests;

// Transferência única do banco de Identity (scripts/database/transfer-identity.sh) em PostgreSQL real: o banco original
// recebe dados reais de Identity, e o procedimento roda como no Compose, no container, com a credencial administrativa.
// Cada teste usa uma origem própria; o destino ouroboros_identity é esvaziado antes (é um banco de teste).
public sealed partial class IdentityDatabaseTransferTests(ProvisionedInstance instance) : IClassFixture<ProvisionedInstance>
{
    private const string Password = "uma frase longa e secreta do cliente";
    private const string NewPassword = "outra frase longa e secreta da nova cliente";
    private const string ParameterKey = "Identity:AdminInvitations:AllowedEmailDomain";
    private static readonly string[] Tables = ["users", "sessions", "refresh_tokens", "email_tokens", "parameters", "parameter_history"];

    internal ProvisionedInstance Instance => instance;

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Transfer_copies_the_whole_identity_database_and_existing_credentials_keep_working()
    {
        await using var scenario = await ScenarioAsync();
        var transfer = await scenario.CutOverAsync();

        // Existentes: senha, sessão e refresh token continuam válidos, agora pelo banco próprio e a role de runtime.
        await using var api = scenario.TargetApi();
        using var client = api.CreateClient();
        var login = await LoginAsync(client, scenario.CustomerEmail, Password);
        Assert.Equal(HttpStatusCode.OK, login.Status);
        Assert.Equal(HttpStatusCode.OK, (await GetMeAsync(client, scenario.Tokens.AccessToken)).StatusCode);
        var refreshed = await client.PostAsJsonAsync("/api/v1/identity/sessions/refresh", new { scenario.Tokens.RefreshToken }, Cancellation);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);

        // Parâmetros e histórico: o valor alterado e as revisões vieram juntos.
        await using var scope = api.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var parameter = await context.Parameters.SingleAsync(p => p.Key == ParameterKey, Cancellation);
        Assert.Equal("transferido.example", parameter.Value);
        Assert.Equal(2, await context.ParameterHistory.CountAsync(h => h.ParameterKey == ParameterKey, Cancellation));
        Assert.NotEmpty(transfer.Id);
    }

    [Fact]
    public async Task Backup_requires_the_write_free_window_and_is_restored_and_compared()
    {
        await using var scenario = await ScenarioAsync();

        var early = await instance.TransferAsync(scenario.Source, "backup");
        Assert.NotEqual(0, early.ExitCode);
        Assert.Contains("janela sem escrita", early.Stderr, StringComparison.Ordinal);

        Succeeded(await instance.TransferAsync(scenario.Source, "freeze"));
        var backup = await instance.TransferAsync(scenario.Source, "backup");

        Succeeded(backup);
        Assert.Contains("restaurado em banco de verificação e idêntico", backup.Stdout, StringComparison.Ordinal);
        var id = IdOf(backup);
        Assert.Equal(0, (await instance.ExecAsync("test", "-s", $"/tmp/transfers/identity-{id}/source.dump")).ExitCode);
        Assert.Equal(0, (await instance.ExecAsync("test", "-s", $"/tmp/transfers/identity-{id}/restore-check.digest")).ExitCode);

        // O banco de verificação não sobra, e a origem segue intacta.
        await using var admin = instance.ConnectAdmin("postgres");
        await admin.OpenAsync(Cancellation);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE datname LIKE 'identity_transfer_check_%'", admin);
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync(Cancellation))!);
    }

    [Fact]
    public async Task Validation_failure_prevents_the_switch_and_leaves_the_origin_intact()
    {
        await using var scenario = await ScenarioAsync();
        var transfer = await scenario.CopyAsync();
        var originBefore = await instance.DigestAsync(scenario.Source);

        // Divergência no destino (por exemplo, uma cópia corrompida).
        await instance.ExecuteAdminAsync(ProvisionedInstance.IdentityDatabase, "UPDATE identity.users SET password_hash = 'adulterado' WHERE email = '" + scenario.CustomerEmail + "'");
        var validation = await instance.TransferAsync(scenario.Source, "validate", transfer.Id);
        var cutover = await instance.TransferAsync(scenario.Source, "cutover", transfer.Id);

        Assert.NotEqual(0, validation.ExitCode);
        Assert.Contains("NÃO troque a connection string", validation.Stderr, StringComparison.Ordinal);
        Assert.Contains("users.password_hash", validation.Stdout, StringComparison.Ordinal);
        Assert.NotEqual(0, cutover.ExitCode);
        Assert.NotEqual(0, (await instance.ExecAsync("test", "-e", $"/tmp/transfers/identity-{transfer.Id}/cutover-ready")).ExitCode);
        Assert.Equal(originBefore, await instance.DigestAsync(scenario.Source));
    }

    [Fact]
    public async Task Copy_refuses_a_target_that_already_has_identity_and_an_origin_that_changed_after_the_backup()
    {
        await using var scenario = await ScenarioAsync();
        var transfer = await scenario.CopyAsync();

        var again = await instance.TransferAsync(scenario.Source, "copy", transfer.Id);
        Assert.NotEqual(0, again.ExitCode);
        Assert.Contains("já tem o schema identity", again.Stderr, StringComparison.Ordinal);

        // Escrita na origem depois do backup (um cliente que ignorou a janela, usando a credencial administrativa).
        await instance.ExecuteAdminAsync(ProvisionedInstance.IdentityDatabase, "DROP SCHEMA identity CASCADE");
        await using (var connection = instance.ConnectAdmin(scenario.Source))
        {
            await connection.OpenAsync(Cancellation);
            await WriteIgnoringTheWindowAsync(connection, "UPDATE identity.users SET failed_login_count = 3");
        }

        var changed = await instance.TransferAsync(scenario.Source, "copy", transfer.Id);
        Assert.NotEqual(0, changed.ExitCode);
        Assert.Contains("origem mudou depois do backup", changed.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Freeze_requires_no_active_sessions_unless_forced()
    {
        await using var scenario = await ScenarioAsync();
        await using var session = instance.ConnectAdmin(scenario.Source);
        await session.OpenAsync(Cancellation);

        var refused = await instance.TransferAsync(scenario.Source, "freeze");
        Assert.NotEqual(0, refused.ExitCode);
        Assert.Contains("sessão", refused.Stderr, StringComparison.Ordinal);

        var forced = await instance.TransferAsync(scenario.Source, "freeze", extra: new Dictionary<string, string> { ["FORCE_TERMINATE"] = "1" });
        Succeeded(forced);
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var probe = new NpgsqlCommand("SELECT 1", session);
            await probe.ExecuteScalarAsync(Cancellation);
        });
    }

    [Fact]
    public async Task Rollback_before_new_writes_only_returns_the_connection_string()
    {
        await using var scenario = await ScenarioAsync();
        var transfer = await scenario.CutOverAsync();

        var rollback = await instance.TransferAsync(scenario.Source, "rollback", transfer.Id);

        Succeeded(rollback);
        Assert.Contains("Sem novas escritas", rollback.Stdout, StringComparison.Ordinal);
        Succeeded(await instance.TransferAsync(scenario.Source, "unfreeze"));

        // A origem voltou a aceitar escrita e o login com a connection string anterior continua válido.
        await using var api = scenario.SourceApi();
        using var client = api.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, scenario.CustomerEmail, Password)).Status);
    }

    [Fact]
    public async Task Rollback_after_new_writes_preserves_and_reconciles_them_into_the_origin()
    {
        await using var scenario = await ScenarioAsync();
        var transfer = await scenario.CutOverAsync();

        // Escritas feitas no banco próprio depois da troca: usuário novo, login, renovação, parâmetro e bloqueio.
        string newRefreshToken;
        await using (var api = scenario.TargetApi())
        {
            await using (var scope = api.Services.CreateAsyncScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
                var hash = scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(NewPassword);
                context.Users.Add(User.Create("nova@example.com", hash, UserRole.Customer));
                await context.SaveChangesAsync(Cancellation);
                await scope.ServiceProvider.GetRequiredService<ParameterWriter>()
                    .ChangeAsync(ParameterKey, "depois-da-troca.example", "Alterado depois da troca.", Guid.CreateVersion7(), Cancellation);
            }

            using var client = api.CreateClient();
            var fresh = await LoginAsync(client, "nova@example.com", NewPassword);
            Assert.Equal(HttpStatusCode.OK, fresh.Status);
            newRefreshToken = fresh.Tokens!.RefreshToken;
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, scenario.CustomerEmail, "senha errada qualquer")).Status);
        }

        NpgsqlConnection.ClearAllPools();
        var rollback = await instance.TransferAsync(scenario.Source, "rollback", transfer.Id);
        Succeeded(rollback);
        Assert.Contains("exportadas e preservadas", rollback.Stdout, StringComparison.Ordinal);
        foreach (var table in new[] { "users", "sessions", "refresh_tokens", "parameters", "parameter_history" })
        {
            Assert.Equal(0, (await instance.ExecAsync("test", "-s", $"/tmp/transfers/identity-{transfer.Id}/reconcile/{table}.csv")).ExitCode);
        }

        // O banco próprio continua com tudo: nada foi apagado nem desfeito.
        Assert.Equal(1L, await CountAsync(ProvisionedInstance.IdentityDatabase, "SELECT count(*) FROM identity.users WHERE email = 'nova@example.com'"));

        var reconcile = await instance.TransferAsync(scenario.Source, "reconcile", transfer.Id);
        Succeeded(reconcile);
        Assert.Contains("aplicadas na origem e verificadas", reconcile.Stdout, StringComparison.Ordinal);
        Assert.Equal(1L, await CountAsync(scenario.Source, "SELECT count(*) FROM identity.users WHERE email = 'nova@example.com'"));

        // Voltando à connection string anterior: usuário, sessão, refresh token e parâmetro criados depois da troca valem.
        await using var origin = scenario.SourceApi();
        using var originClient = origin.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(originClient, "nova@example.com", NewPassword)).Status);
        var renewed = await originClient.PostAsJsonAsync("/api/v1/identity/sessions/refresh", new { RefreshToken = newRefreshToken }, Cancellation);
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
        await using var originScope = origin.Services.CreateAsyncScope();
        var originContext = originScope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        Assert.Equal("depois-da-troca.example", (await originContext.Parameters.SingleAsync(p => p.Key == ParameterKey, Cancellation)).Value);
        Assert.Equal(3, await originContext.ParameterHistory.CountAsync(h => h.ParameterKey == ParameterKey, Cancellation));
    }

    [Fact]
    public async Task Reconcile_refuses_conflicts_when_the_origin_was_written_after_the_switch()
    {
        await using var scenario = await ScenarioAsync();
        var transfer = await scenario.CutOverAsync();
        await instance.ExecuteAdminAsync(ProvisionedInstance.IdentityDatabase, "UPDATE identity.users SET failed_login_count = 2 WHERE email = '" + scenario.CustomerEmail + "'");
        Succeeded(await instance.TransferAsync(scenario.Source, "rollback", transfer.Id));

        // Alguém escreveu na origem (credencial administrativa ignorando a janela) depois da troca.
        await using (var connection = instance.ConnectAdmin(scenario.Source))
        {
            await connection.OpenAsync(Cancellation);
            await WriteIgnoringTheWindowAsync(connection, "UPDATE identity.users SET failed_login_count = 4");
        }

        var before = await CountAsync(scenario.Source, "SELECT sum(failed_login_count) FROM identity.users");
        var reconcile = await instance.TransferAsync(scenario.Source, "reconcile", transfer.Id);

        Assert.NotEqual(0, reconcile.ExitCode);
        Assert.Contains("conflito", reconcile.Stderr, StringComparison.Ordinal);
        Assert.Equal(before, await CountAsync(scenario.Source, "SELECT sum(failed_login_count) FROM identity.users"));
    }

    [Fact]
    public async Task Runtime_role_cannot_run_migrations_and_the_migration_role_can()
    {
        await instance.ExecuteAdminAsync(ProvisionedInstance.IdentityDatabase, "DROP SCHEMA IF EXISTS identity CASCADE");

        await using (var runtime = new IntegrationApiFactory(instance.ConnectionStringFor(ProvisionedInstance.IdentityRuntime, ProvisionedInstance.IdentityDatabase)))
        {
            await using var scope = runtime.Services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            var denied = await Assert.ThrowsAsync<PostgresException>(() => context.Database.MigrateAsync(Cancellation));
            Assert.Equal("42501", denied.SqlState);
        }

        await using (var migration = new IntegrationApiFactory(instance.ConnectionStringFor(ProvisionedInstance.IdentityMigrator, ProvisionedInstance.IdentityDatabase)))
        {
            await using var scope = migration.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Database.MigrateAsync(Cancellation);
        }

        // Com a estrutura criada pela role de migration, o runtime já escreve, mas continua sem DDL.
        await using var api = new IntegrationApiFactory(instance.ConnectionStringFor(ProvisionedInstance.IdentityRuntime, ProvisionedInstance.IdentityDatabase));
        await using var apiScope = api.Services.CreateAsyncScope();
        var runtimeContext = apiScope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        runtimeContext.Users.Add(User.Create("runtime@example.com", "hash", UserRole.Customer));
        await runtimeContext.SaveChangesAsync(Cancellation);
        Assert.Equal(1L, await CountAsync(ProvisionedInstance.IdentityDatabase, "SELECT count(*) FROM identity.users"));
    }

    private async Task<Scenario> ScenarioAsync(bool seed = true)
    {
        await instance.ExecuteAdminAsync(ProvisionedInstance.IdentityDatabase, "DROP SCHEMA IF EXISTS identity CASCADE");
        var source = $"src_{Guid.NewGuid():N}";
        await instance.ExecuteAdminAsync("postgres", $"CREATE DATABASE \"{source}\"");

        // Como o banco original da instância depois do provisionamento: ninguém além do administrador conecta.
        await instance.ExecuteAdminAsync("postgres", $"REVOKE CONNECT ON DATABASE \"{source}\" FROM PUBLIC");
        var scenario = new Scenario(this, source);
        await scenario.SeedAsync(seed);
        return scenario;
    }

    // A janela vale por padrão para novas sessões; quem a ignora precisa reabrir a escrita explicitamente.
    private static async Task WriteIgnoringTheWindowAsync(NpgsqlConnection connection, string sql)
    {
        await using (var reopen = new NpgsqlCommand("SET default_transaction_read_only = off", connection))
        {
            await reopen.ExecuteNonQueryAsync(Cancellation);
        }

        await using var write = new NpgsqlCommand(sql, connection);
        await write.ExecuteNonQueryAsync(Cancellation);
    }

    private static void Succeeded(ProvisionedInstance.ScriptResult result) =>
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);

    private static string IdOf(ProvisionedInstance.ScriptResult result) =>
        TransferId().Match(result.Stdout) is { Success: true } match ? match.Groups[1].Value : throw new InvalidOperationException(result.Stdout + result.Stderr);

    [GeneratedRegex(@"Transferência: (\S+)")]
    private static partial Regex TransferId();

    private async Task<long> CountAsync(string database, string sql)
    {
        await using var connection = instance.ConnectAdmin(database);
        await connection.OpenAsync(Cancellation);
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Cancellation), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<(HttpStatusCode Status, Tokens? Tokens)> LoginAsync(HttpClient client, string email, string password)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/identity/sessions", new { Email = email, Password = password }, Cancellation);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return (response.StatusCode, null);
        }

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Cancellation);
        return (response.StatusCode, new Tokens(body.GetProperty("accessToken").GetString()!, body.GetProperty("refreshToken").GetString()!));
    }

    private static async Task<HttpResponseMessage> GetMeAsync(HttpClient client, string accessToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/identity/me");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        return await client.SendAsync(request, Cancellation);
    }

    private sealed record Tokens(string AccessToken, string RefreshToken);

    private sealed record Transfer(string Id);

    // Origem com dados reais de Identity, criados pela própria API: usuários com hash de senha, sessão e refresh token,
    // token de e-mail e parâmetro alterado (com histórico).
    private sealed class Scenario(IdentityDatabaseTransferTests owner, string source) : IAsyncDisposable
    {
        public string Source => source;

        public string CustomerEmail => "cliente@example.com";

        public Tokens Tokens { get; private set; } = null!;

        public ApiFactory SourceApi() => new IntegrationApiFactory(owner.Instance.ConnectionStringFor("postgres", source));

        public ApiFactory TargetApi() => new IntegrationApiFactory(
            owner.Instance.ConnectionStringFor(ProvisionedInstance.IdentityRuntime, ProvisionedInstance.IdentityDatabase));

        public async Task SeedAsync(bool seed)
        {
            await using var api = SourceApi();
            if (!seed)
            {
                // Origem recém-migrada: sequências nunca usadas e tabelas só com os parâmetros iniciais.
                await using var migrationScope = api.Services.CreateAsyncScope();
                await migrationScope.ServiceProvider.GetRequiredService<IdentityDbContext>().Database.MigrateAsync(Cancellation);
                NpgsqlConnection.ClearAllPools();
                return;
            }

            await using (var scope = api.Services.CreateAsyncScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
                await context.Database.MigrateAsync(Cancellation);
                var hash = scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(Password);
                context.Users.Add(User.Create(CustomerEmail, hash, UserRole.Customer));
                context.Users.Add(User.Create("oi@adnerscarpelini.com", hash, UserRole.Admin));
                await context.SaveChangesAsync(Cancellation);
                await scope.ServiceProvider.GetRequiredService<ParameterWriter>()
                    .ChangeAsync(ParameterKey, "transferido.example", "Alterado antes da transferência.", Guid.CreateVersion7(), Cancellation);
            }

            using var client = api.CreateClient();
            var login = await LoginAsync(client, CustomerEmail, Password);
            Assert.Equal(HttpStatusCode.OK, login.Status);
            Tokens = login.Tokens!;
            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/v1/identity/registrations", new { Email = "pendente@example.com" }, Cancellation)).StatusCode);
            NpgsqlConnection.ClearAllPools();
        }

        public async Task<Transfer> CopyAsync()
        {
            IdentityDatabaseTransferTests.Succeeded(await owner.Instance.TransferAsync(source, "freeze"));
            var backup = await owner.Instance.TransferAsync(source, "backup");
            Succeeded(backup);
            var id = IdOf(backup);
            var copy = await owner.Instance.TransferAsync(source, "copy", id);
            Assert.True(copy.ExitCode == 0, copy.Stdout + copy.Stderr);
            Assert.Contains("Validação: origem e destino idênticos", copy.Stdout, StringComparison.Ordinal);
            return new Transfer(id);
        }

        public async Task<Transfer> CutOverAsync()
        {
            var transfer = await CopyAsync();
            var cutover = await owner.Instance.TransferAsync(source, "cutover", transfer.Id);
            Assert.True(cutover.ExitCode == 0, cutover.Stdout + cutover.Stderr);
            Assert.Contains("Troca liberada", cutover.Stdout, StringComparison.Ordinal);
            return transfer;
        }

        public ValueTask DisposeAsync()
        {
            NpgsqlConnection.ClearAllPools();
            return ValueTask.CompletedTask;
        }
    }
}
