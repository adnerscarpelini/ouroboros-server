using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Ouroboros.Notifications.Application;
using Ouroboros.Notifications.Contracts;
using Ouroboros.Notifications.Infrastructure;
using Ouroboros.Notifications.Infrastructure.Email;
using Ouroboros.Notifications.Infrastructure.Persistence;

namespace Ouroboros.Notifications;

public static class DependencyInjection
{
    public static IServiceCollection AddNotificationsService(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IValidateOptions<NotificationOptions>, NotificationOptionsValidator>();
        services.AddOptions<NotificationOptions>().BindConfiguration(NotificationOptions.SectionName).ValidateOnStart();
        services.AddSingleton<IEmailPayloadReader, ProtectedEmailPayloadReader>();
        services.AddSingleton<PickupEmailTransport>();
        services.AddSingleton<UnconfiguredEmailTransport>();
        services.AddSingleton<IEmailTransport>(provider => provider.GetRequiredService<IOptions<NotificationOptions>>().Value.Email.Provider == "PickupDirectory"
            ? provider.GetRequiredService<PickupEmailTransport>() : provider.GetRequiredService<UnconfiguredEmailTransport>());
        services.AddScoped<INotificationDelivery, NotificationDelivery>();
        services.AddDbContext<NotificationsDbContext>((provider, database) => database.UseNpgsql(
            provider.GetRequiredService<IConfiguration>().GetConnectionString("Notifications"),
            postgres => postgres.MigrationsHistoryTable("__EFMigrationsHistory", NotificationsDbContext.Schema)));
        services.AddSingleton<MessageFingerprints>();
        services.AddSingleton<InboxMetrics>();
        services.AddScoped<InboxAcceptance>();
        services.AddScoped<InboxExecutionStore>();
        services.AddScoped<FailureDispatchStore>();
        services.AddHostedService<EmailConsumer>();
        services.AddHostedService<InboxExecutor>();
        services.AddHostedService<InboxMaintenance>();
        services.AddHostedService<FailureReferenceRelay>();
        return services;
    }
}
