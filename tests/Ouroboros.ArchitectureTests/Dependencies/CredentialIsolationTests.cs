namespace Ouroboros.ArchitectureTests.Dependencies;

// Credenciais de banco: a administrativa da instância nunca chega ao código dos serviços e o modelo
// versionado traz apenas valores de exemplo. O isolamento efetivo entre bancos é testado com
// PostgreSQL real em DatabaseProvisioningTests.
public sealed class CredentialIsolationTests
{
    private static readonly string Root = SourceProjects.Current.Root;

    [Fact]
    public void Administrative_credential_is_not_referenced_by_services_or_executables()
    {
        var offending = Directory.EnumerateFiles(Path.Combine(Root, "src"), "*", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(file) && Path.GetExtension(file) is ".cs" or ".json" or ".csproj" or ".props" or ".yml" or ".yaml" or ".config")
            .Where(file => File.ReadAllText(file).Contains("POSTGRES_PASSWORD", StringComparison.Ordinal)
                || File.ReadAllText(file).Contains("POSTGRES_USER", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(Root, file))
            .ToList();

        Assert.Empty(offending);
    }

    [Fact]
    public void Env_example_contains_only_placeholder_passwords_and_env_files_are_ignored()
    {
        var passwords = File.ReadAllLines(Path.Combine(Root, ".env.example"))
            .Where(line => !line.TrimStart().StartsWith('#') && line.Contains("PASSWORD=", StringComparison.Ordinal))
            .Select(line => line.Split('=', 2))
            .ToList();

        Assert.NotEmpty(passwords);
        Assert.All(passwords, pair => Assert.StartsWith("troque-", pair[1], StringComparison.Ordinal));
        Assert.Equal(passwords.Count, passwords.Select(pair => pair[1]).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(File.ReadAllLines(Path.Combine(Root, ".gitignore")), line => line.Trim() == "*.env");
    }

    [Fact]
    public void Service_credentials_are_separate_from_the_administrative_one_in_the_env_example()
    {
        var keys = File.ReadAllLines(Path.Combine(Root, ".env.example"))
            .Where(line => !line.TrimStart().StartsWith('#') && line.Contains('='))
            .Select(line => line.Split('=', 2)[0])
            .ToList();

        foreach (var service in new[] { "IDENTITY", "NOTIFICATIONS" })
        {
            Assert.Contains($"{service}_RUNTIME_PASSWORD", keys);
            Assert.Contains($"{service}_MIGRATION_PASSWORD", keys);
        }

        Assert.Contains("POSTGRES_PASSWORD", keys);
    }

    private static bool IsBuildOutput(string file) =>
        file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
}
