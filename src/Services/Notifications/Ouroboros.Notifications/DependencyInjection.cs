using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Ouroboros.Notifications.Application;
using Ouroboros.Notifications.Contracts;
using Ouroboros.Notifications.Infrastructure;
using Ouroboros.Notifications.Infrastructure.Email;

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
        services.AddHostedService<EmailConsumer>();
        return services;
    }
}
