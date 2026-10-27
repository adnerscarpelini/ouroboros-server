using Ouroboros.ArchitectureTests.Dependencies;

namespace Ouroboros.ArchitectureTests;

public sealed class OriginalDatabaseArchitectureTests
{
    private static readonly string Root = SourceProjects.Current.Root;

    [Fact]
    public void Decommission_script_exists_uses_lf_and_enforces_explicit_approval_backup_and_non_destructive_lock()
    {
        var scriptPath = Path.Combine(Root, "scripts", "database", "decommission-original-db.sh");
        Assert.True(File.Exists(scriptPath), "scripts/database/decommission-original-db.sh deve existir.");

        var rawBytes = File.ReadAllBytes(scriptPath);
        Assert.DoesNotContain((byte)'\r', rawBytes);

        var script = File.ReadAllText(scriptPath);
        Assert.Contains("cmd_inventory()", script, StringComparison.Ordinal);
        Assert.Contains("cmd_verify_unused()", script, StringComparison.Ordinal);
        Assert.Contains("cmd_final_backup()", script, StringComparison.Ordinal);
        Assert.Contains("cmd_rotate_admin_credential()", script, StringComparison.Ordinal);
        Assert.Contains("cmd_deactivate()", script, StringComparison.Ordinal);
        Assert.Contains("cmd_reactivate()", script, StringComparison.Ordinal);
        Assert.Contains("session-safety.sql", script, StringComparison.Ordinal);
        Assert.Contains("CONFIRM_ORIGINAL_DB_DEACTIVATION", script, StringComparison.Ordinal);
        Assert.Contains("RETENTION_DAYS", script, StringComparison.Ordinal);
        Assert.Contains("final-backup-verified", script, StringComparison.Ordinal);
        Assert.Contains("ALLOW_CONNECTIONS false CONNECTION LIMIT 0", script, StringComparison.Ordinal);

        // Apenas o banco temporário de verificação do backup ($CHECK_DATABASE) pode receber DROP DATABASE.
        var dropLines = script
            .Split('\n')
            .Where(line => !line.TrimStart().StartsWith('#') && line.Contains("DROP DATABASE", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Single(dropLines);
        Assert.Contains("$CHECK_DATABASE", dropLines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_and_docs_never_drop_original_database_or_volumes_as_side_effect()
    {
        var compose = File.ReadAllText(Path.Combine(Root, "compose.yaml"));
        Assert.Contains("decommission-original-db:", compose, StringComparison.Ordinal);
        Assert.Contains("profiles: [\"decommission\"]", compose, StringComparison.Ordinal);
        Assert.Contains("/scripts/decommission-original-db.sh", compose, StringComparison.Ordinal);

        var filesToScan = Directory.EnumerateFiles(Path.Combine(Root, "docs"), "*.md", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(Root, "scripts"), "*", SearchOption.AllDirectories))
            .Append(Path.Combine(Root, "compose.yaml"));

        var forbiddenVolumeCommands = new[]
        {
            "docker compose down -v",
            "docker compose down --volumes",
            "docker-compose down -v",
            "docker-compose down --volumes",
            "docker volume rm",
            "docker volume prune",
        };

        foreach (var file in filesToScan)
        {
            var content = File.ReadAllText(file);
            foreach (var forbidden in forbiddenVolumeCommands)
            {
                Assert.DoesNotContain(forbidden, content, StringComparison.OrdinalIgnoreCase);
            }
        }

        var persistenceDoc = File.ReadAllText(Path.Combine(Root, "docs", "architecture", "persistence.md"));
        Assert.Contains("## Destino e desativação do banco original (#97)", persistenceDoc, StringComparison.Ordinal);
        Assert.Contains("settings.parameters", persistenceDoc, StringComparison.Ordinal);
        Assert.Contains("settings.parameter_history", persistenceDoc, StringComparison.Ordinal);
        Assert.Contains("__EFMigrationsHistory", persistenceDoc, StringComparison.Ordinal);
        Assert.Contains("RETENTION_DAYS", persistenceDoc, StringComparison.Ordinal);
        Assert.Contains("CONFIRM_ORIGINAL_DB_DEACTIVATION", persistenceDoc, StringComparison.Ordinal);
    }
}
