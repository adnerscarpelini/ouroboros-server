using System.Diagnostics;
using Asp.Versioning;
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

// A versão maior vem sempre na URL (api/v{version:apiVersion}/...) e não há versão implícita.
builder.Services.AddApiVersioning(options =>
{
    options.ApiVersionReader = new UrlSegmentApiVersionReader();
    options.ReportApiVersions = true;
}).AddMvc();

// Todo erro sai em application/problem+json (RFC 9457) com o trace id W3C da requisição, o mesmo
// dos logs. Falhas de negócio definem o próprio type nos controllers dos módulos.
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
    context.ProblemDetails.Extensions["traceId"] = Activity.Current?.Id ?? context.HttpContext.TraceIdentifier);

builder.Services.AddIdentityModule();
builder.Services.AddCustomersModule();
builder.Services.AddAccountsModule();
builder.Services.AddTransactionsModule();

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.MapControllers();
app.Run();

// Expõe o ponto de entrada aos testes com WebApplicationFactory.
public partial class Program;
