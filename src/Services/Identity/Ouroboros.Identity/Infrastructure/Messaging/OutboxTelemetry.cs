using System.Diagnostics;

namespace Ouroboros.Identity.Infrastructure.Messaging;

internal static class OutboxTelemetry
{
    internal const string SourceName = "Ouroboros.Identity.Outbox";
    internal static readonly ActivitySource Source = new(SourceName, "1.0.0");
}
