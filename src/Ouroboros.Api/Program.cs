using Ouroboros.Hosting;
using Ouroboros.Identity;
using Ouroboros.Modules.Accounts;
using Ouroboros.Modules.Customers;
using Ouroboros.Modules.Settings;
using Ouroboros.Modules.Transactions;

// API externa: aplicação do cliente, endpoints anônimos e integrações de terceiros.
var builder = WebApplication.CreateBuilder(args);
builder.AddOuroborosHost(HostAudience.External);

builder.Services.AddIdentityModule();
builder.Services.AddSettingsModule();
builder.Services.AddCustomersModule();
builder.Services.AddAccountsModule();
builder.Services.AddTransactionsModule();

var app = builder.Build();
app.UseOuroborosHost();
app.Run();

// Expõe o ponto de entrada aos testes com WebApplicationFactory.
public partial class Program;
