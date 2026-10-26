using Ouroboros.Identity;

namespace Ouroboros.Identity.Api.CommandLine;

// Comando explícito para aplicar migrations do banco de Identity sem subir o servidor HTTP:
//   dotnet Ouroboros.Identity.Api.dll migrate
// Usa a connection string de ConnectionStrings__Identity (ou ConnectionStrings__Ouroboros) com a role de migration.
public static class MigrateDatabaseCommand
{
    public const string Name = "migrate";
    public const int Success = 0;
    public const int Failure = 1;

    public static bool Matches(string[] args) => args.Length == 1 && args[0] == Name;

    public static async Task<int> RunAsync(
        Func<string, string?> getEnvironmentVariable,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var connectionString = getEnvironmentVariable("ConnectionStrings__Identity");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            connectionString = getEnvironmentVariable("ConnectionStrings__Ouroboros");
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            await error.WriteLineAsync("Defina ConnectionStrings__Identity (role identity_migrator) para aplicar migrations.");
            return Failure;
        }

        try
        {
            await DependencyInjection.MigrateDatabaseAsync(connectionString, cancellationToken);
            await output.WriteLineAsync("Migrations de Identity aplicadas.");
            return Success;
        }
        catch (Exception)
        {
            await error.WriteLineAsync("Falha ao aplicar migrations de Identity; detalhes sensíveis omitidos.");
            return Failure;
        }
    }
}
