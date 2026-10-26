using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Ouroboros.Identity.Contracts.Administrators;

namespace Ouroboros.Identity.Api.CommandLine;

// Comando de linha da API de Identity para criar o primeiro administrador:
//   dotnet run --project src/Services/Identity/Ouroboros.Identity.Api -- create-admin --email <e-mail>
// A senha vem da variável de ambiente OUROBOROS_ADMIN_PASSWORD, nunca de argumento (argumentos ficam
// no histórico do shell e na lista de processos), e não aparece na saída.
public static class CreateAdminCommand
{
    public const string Name = "create-admin";
    public const string PasswordVariable = "OUROBOROS_ADMIN_PASSWORD";

    public const int Success = 0;
    public const int Failure = 1;
    public const int UsageError = 2;

    public static bool Matches(string[] args) => args.Length > 0 && args[0] == Name;

    public static async Task<int> RunAsync(
        IServiceProvider services,
        string[] args,
        Func<string, string?> getEnvironmentVariable,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var email = ReadOption(args, "--email");
        if (string.IsNullOrWhiteSpace(email))
        {
            await error.WriteLineAsync($"Uso: {Name} --email <e-mail>   (senha na variável {PasswordVariable})");
            return UsageError;
        }

        var password = getEnvironmentVariable(PasswordVariable);
        if (string.IsNullOrEmpty(password))
        {
            await error.WriteLineAsync($"Defina a senha na variável de ambiente {PasswordVariable}. Nada foi criado.");
            return UsageError;
        }

        await using var scope = services.CreateAsyncScope();
        using var activity = new Activity("create-admin").SetIdFormat(ActivityIdFormat.W3C).Start();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(CreateAdminCommand));
        CreateFirstAdminResult result;
        try
        {
            result = await scope.ServiceProvider.GetRequiredService<ICreateFirstAdmin>()
                .ExecuteAsync(new CreateFirstAdminRequest(email, password), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Comando create-admin cancelado: expected-cancellation.");
            await error.WriteLineAsync("Comando cancelado.");
            return Failure;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Falha no comando create-admin: command-failed.");
            await error.WriteLineAsync("Não foi possível concluir o comando. Consulte os diagnósticos correlacionados.");
            return Failure;
        }

        if (result.Succeeded)
        {
            await output.WriteLineAsync(result.Message);
            return Success;
        }

        await error.WriteLineAsync($"{result.Message} Nada foi criado.");
        return Failure;
    }

    private static string? ReadOption(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
