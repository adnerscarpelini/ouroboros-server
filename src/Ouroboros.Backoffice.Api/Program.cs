using Ouroboros.Backoffice.Api.CommandLine;
using Ouroboros.Hosting;
using Ouroboros.Modules.Accounts;
using Ouroboros.Modules.Customers;
using Ouroboros.Modules.Identity;
using Ouroboros.Modules.Settings;
using Ouroboros.Modules.Transactions;

// API de backoffice: só os controllers de administração dos módulos. Em deploy, é publicada apenas
// na rede interna.
var builder = WebApplication.CreateBuilder(args);
builder.AddOuroborosHost(HostAudience.Backoffice);

builder.Services.AddIdentityModule();
builder.Services.AddSettingsModule();
builder.Services.AddCustomersModule();
builder.Services.AddAccountsModule();
builder.Services.AddTransactionsModule();

var app = builder.Build();

// Comandos de operação (por exemplo, create-admin) rodam e encerram sem subir o servidor HTTP.
if (CreateAdminCommand.Matches(args))
{
    return await CreateAdminCommand.RunAsync(
        app.Services, args, Environment.GetEnvironmentVariable, Console.Out, Console.Error, CancellationToken.None);
}

app.UseOuroborosHost();
app.Run();
return 0;
