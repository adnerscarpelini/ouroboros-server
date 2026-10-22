using Microsoft.AspNetCore.Mvc.Controllers;
using Ouroboros.Api;
using Ouroboros.Modules.Accounts;
using Ouroboros.Modules.Customers;
using Ouroboros.Modules.Identity;
using Ouroboros.Modules.Transactions;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers().ConfigureApplicationPartManager(manager =>
{
    manager.FeatureProviders.Remove(manager.FeatureProviders.OfType<ControllerFeatureProvider>().Single());
    manager.FeatureProviders.Add(new ModuleControllerFeatureProvider());
});
builder.Services.AddIdentityModule();
builder.Services.AddCustomersModule();
builder.Services.AddAccountsModule();
builder.Services.AddTransactionsModule();

var app = builder.Build();
app.MapControllers();
app.Run();

// Expõe o ponto de entrada aos testes com WebApplicationFactory.
public partial class Program;
