using Ouroboros.Identity.Api;
using Ouroboros.Identity.Api.CommandLine;

// API de Identity: clientes e integrações no listener público, administradores no listener administrativo
// (por padrão só em loopback). Os dois públicos compartilham processo, banco e casos de uso.
var app = IdentityApiHost.Build(args);

// Comandos de operação (por exemplo, create-admin) rodam e encerram sem subir o servidor HTTP.
if (CreateAdminCommand.Matches(args))
{
    return await CreateAdminCommand.RunAsync(
        app.Services, args, Environment.GetEnvironmentVariable, Console.Out, Console.Error, CancellationToken.None);
}

app.UsePipeline();
app.Run();
return 0;
