using Microsoft.Extensions.Hosting;
using Ouroboros.Notifications;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Services.AddNotificationsService();
await builder.Build().RunAsync();
