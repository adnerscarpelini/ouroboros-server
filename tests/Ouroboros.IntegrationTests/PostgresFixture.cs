using System.Diagnostics;
using System.Security.Cryptography;
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

    public async Task<string> CreateDatabaseAsync(bool useTemplate = true)
    {
        var name = $"ouroboros_test_{Guid.NewGuid():N}";
        await _createLock.WaitAsync();
        try
        {
            var template = useTemplate ? TemplateDatabase : "template0";
            await ExecuteAsync($"""CREATE DATABASE "{name}" TEMPLATE "{template}" """);
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

    // Backup e restauração reais, somente dos bancos isolados que a fixture criou.
    public async Task BackupAndRestoreAsync(string source, string destination)
    {
        if (!source.StartsWith("ouroboros_test_", StringComparison.Ordinal)
            || !destination.StartsWith("ouroboros_test_", StringComparison.Ordinal))
        {
            throw new ArgumentException("Backup de teste exige bancos da fixture.");
        }

        var path = $"/tmp/{Guid.NewGuid():N}.dump";
        var username = new NpgsqlConnectionStringBuilder(_container.GetConnectionString()).Username!;
        var backup = await _container.ExecAsync(["pg_dump", "--username", username, "--format=custom", "--no-owner", "--no-acl", "--file", path, source]);
        Assert.Equal(0, backup.ExitCode);
        // Exercita também o formato cifrado e autenticado usado para conservar backups com intenções.
        var directory = Path.Combine(Path.GetTempPath(), "ouroboros-protected-backup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var dump = Path.Combine(directory, "source.dump");
            var key = Path.Combine(directory, "backup.key");
            var managed = Path.Combine(directory, "managed");
            var restored = Path.Combine(directory, "restored.dump");
            await File.WriteAllBytesAsync(dump, await _container.ReadFileAsync(path));
            await File.WriteAllBytesAsync(key, RandomNumberGenerator.GetBytes(32));
            await BackupToolAsync("Protect", dump, managed, key, null);
            await BackupToolAsync("Restore", Directory.GetFiles(managed, "*.json").Single(), managed, key, restored);
            Assert.Equal(await File.ReadAllBytesAsync(dump), await File.ReadAllBytesAsync(restored));
            await _container.CopyAsync(await File.ReadAllBytesAsync(restored), path);
        }
        finally { Directory.Delete(directory, recursive: true); }
        var restore = await _container.ExecAsync(["pg_restore", "--username", username, "--exit-on-error", "--no-owner", "--no-acl", "--dbname", destination, path]);
        Assert.Equal(0, restore.ExitCode);
    }

    private static async Task BackupToolAsync(string mode, string source, string directory, string key, string? restored)
    {
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-File", RepositoryRoot.Resolve("scripts", "messaging", "protected-email-backup.ps1"),
            "-Mode", mode, "-SourceFile", source, "-Directory", directory, "-KeyPath", key, "-KeyId", "test-backup" })
        { start.ArgumentList.Add(argument); }
        if (restored is not null) { start.ArgumentList.Add("-RestoreFile"); start.ArgumentList.Add(restored); }
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        await Task.WhenAll(stdout, stderr);
        Assert.True(process.ExitCode == 0, "Ferramenta de backup protegido falhou; detalhes omitidos.");
    }

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
