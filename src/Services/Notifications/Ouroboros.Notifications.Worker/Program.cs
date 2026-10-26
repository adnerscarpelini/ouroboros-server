using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Ouroboros.Hosting;
using Ouroboros.Notifications;
using Ouroboros.Observability;

// Comando explícito para aplicar migrations de Notifications sem subir o worker.
if (args.Length == 1 && args[0] == "migrate")
{
    var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Notifications");
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        await Console.Error.WriteLineAsync("Defina ConnectionStrings__Notifications (role notifications_migrator) para aplicar migrations.");
        return 1;
    }

    try
    {
        await DependencyInjection.MigrateDatabaseAsync(connectionString, CancellationToken.None);
        await Console.Out.WriteLineAsync("Migrations de Notifications aplicadas.");
        return 0;
    }
    catch (Exception)
    {
        await Console.Error.WriteLineAsync("Falha ao aplicar migrations de Notifications; detalhes sensíveis omitidos.");
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    ApplicationName = typeof(Program).Assembly.GetName().Name,
});
builder.AddOuroborosObservability("notifications", typeof(Program).Assembly);
builder.Services.AddOuroborosHealthChecks();
builder.Services.AddNotificationsService();

var urls = builder.Configuration.GetSection("Notifications:Health:Urls").Get<string[]>();
if (urls is null || urls.Length == 0)
{
    urls = ["http://127.0.0.1:5143"];
}

builder.WebHost.ConfigureKestrel(kestrel =>
{
    foreach (var url in urls)
    {
        var uri = new Uri(url);
        if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            kestrel.ListenLocalhost(uri.Port);
        }
        else
        {
            kestrel.Listen(IPAddress.Parse(uri.Host.Trim('[', ']')), uri.Port);
        }
    }
});

await using var app = builder.Build();
app.MapOuroborosHealthChecks();
await app.RunAsync();
return 0;
