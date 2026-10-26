using Ouroboros.Identity.Api;
using Ouroboros.Identity.Api.CommandLine;

// API de Identity: clientes e integrações no listener público, administradores no listener administrativo
// (por padrão só em loopback). Os dois públicos compartilham processo, banco e casos de uso.
// Comandos de operação (migrate e create-admin) rodam e encerram sem subir o servidor HTTP.
if (MigrateDatabaseCommand.Matches(args))
{
    return await MigrateDatabaseCommand.RunAsync(
        Environment.GetEnvironmentVariable, Console.Out, Console.Error, CancellationToken.None);
}

await using var app = IdentityApiHost.Build(args);

if (CreateAdminCommand.Matches(args))
{
    return await CreateAdminCommand.RunAsync(
        app.Services, args, Environment.GetEnvironmentVariable, Console.Out, Console.Error, CancellationToken.None);
}

app.UsePipeline();
app.Run();
return 0;
