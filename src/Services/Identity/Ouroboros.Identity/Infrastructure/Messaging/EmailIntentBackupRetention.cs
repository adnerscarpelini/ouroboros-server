using Ouroboros.BuildingBlocks.Storage;

namespace Ouroboros.Identity.Infrastructure.Messaging;

internal static class EmailIntentBackupRetention
{
    internal const string Marker = ProtectedEmailBackupRetention.Marker;
    internal static void Purge(string? directory, DateTimeOffset now) => ProtectedEmailBackupRetention.Purge(directory, now);
}
