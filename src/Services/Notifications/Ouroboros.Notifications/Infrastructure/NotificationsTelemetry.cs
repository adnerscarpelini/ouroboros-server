using System.Diagnostics;

namespace Ouroboros.Notifications.Infrastructure;

internal static class NotificationsTelemetry
{
    internal const string SourceName = "Ouroboros.Notifications.Inbox";
    internal static readonly ActivitySource Source = new(SourceName, "1.0.0");
}
