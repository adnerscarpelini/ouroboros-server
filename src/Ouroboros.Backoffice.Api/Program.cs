using Ouroboros.Hosting;
using Ouroboros.Modules.Accounts;
using Ouroboros.Modules.Customers;
using Ouroboros.Modules.Identity;
using Ouroboros.Modules.Transactions;

// API de backoffice: só os controllers de administração dos módulos. Em deploy, é publicada apenas
// na rede interna.
var builder = WebApplication.CreateBuilder(args);
builder.AddOuroborosHost(HostAudience.Backoffice);

builder.Services.AddIdentityModule();
builder.Services.AddCustomersModule();
builder.Services.AddAccountsModule();
builder.Services.AddTransactionsModule();

var app = builder.Build();
app.UseOuroborosHost();
app.Run();
