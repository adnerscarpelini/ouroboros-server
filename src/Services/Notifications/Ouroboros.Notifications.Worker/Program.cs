using Microsoft.Extensions.Hosting;
using Ouroboros.Notifications;
using Ouroboros.Observability;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.AddOuroborosObservability("notifications", typeof(Program).Assembly);
builder.Services.AddNotificationsService();
await builder.Build().RunAsync();
