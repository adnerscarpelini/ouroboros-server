using System.Globalization;

namespace Ouroboros.Identity.Infrastructure.Messaging;

internal static class EmailIntentBackupRetention
{
    internal const string Marker = "ouroboros-protected-backups-v1";

    internal static void Purge(string? directory, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(directory)) { return; }
        if (!Path.IsPathFullyQualified(directory) || new DirectoryInfo(directory).LinkTarget is not null
            || File.ReadAllText(Path.Combine(directory, ".email-intent-backups")) != Marker)
        { throw new InvalidOperationException("Diretório de backups protegidos não reconhecido."); }
        var removed = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "email-intents-*.json", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            if (name.Length != 66 || name[28] != '-' || !Guid.TryParseExact(name.AsSpan(29, 32), "N", out _)
                || !DateTimeOffset.TryParseExact(name.AsSpan(14, 14), "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var created) || created.AddDays(30) > now
                || new FileInfo(path).LinkTarget is not null) { continue; }
            File.Delete(path);
            if (++removed == 100) { break; }
        }
        foreach (var path in Directory.EnumerateFiles(directory, "access-*.jsonl", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            if (name.Length != 21 || !DateTimeOffset.TryParseExact(name.AsSpan(7, 8), "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var day) || day.AddDays(31) > now || new FileInfo(path).LinkTarget is not null)
            { continue; }
            File.Delete(path);
            if (++removed >= 100) { break; }
        }
    }
}
