using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Ouroboros.Notifications.Infrastructure;

internal sealed class MessageFingerprints(IOptions<NotificationOptions> options)
{
    internal (string KeyId, byte[] Value) Compute(ReadOnlySpan<byte> content, string? keyId = null)
    {
        byte[]? key = null;
        try
        {
            var path = options.Value.FingerprintKeyRingPath;
            if (!Path.IsPathFullyQualified(path) || new FileInfo(path).Length > 65_536) { throw new InvalidOperationException(); }
            var ring = JsonSerializer.Deserialize<KeyRing>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidOperationException();
            if (ring.Keys is null || ring.Keys.Length is < 1 or > 16
                || ring.Keys.Any(entry => entry is null || string.IsNullOrWhiteSpace(entry.KeyId) || entry.KeyId.Length > 64)
                || ring.Keys.Select(entry => entry.KeyId).Distinct(StringComparer.Ordinal).Count() != ring.Keys.Length)
            { throw new InvalidOperationException(); }
            var selected = ring.Keys.Single(entry => entry.KeyId == (keyId ?? ring.ActiveKeyId));
            if (!Path.IsPathFullyQualified(selected.KeyPath) || new FileInfo(selected.KeyPath).Length != 32) { throw new InvalidOperationException(); }
            key = File.ReadAllBytes(selected.KeyPath);
            if (key.Length != 32) { throw new InvalidOperationException(); }
            return (selected.KeyId, HMACSHA256.HashData(key, content));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        { throw new InvalidOperationException("Chave de impressão autenticada indisponível ou inválida."); }
        finally { if (key is not null) { CryptographicOperations.ZeroMemory(key); } }
    }

    private sealed record KeyRing(string ActiveKeyId, KeyEntry[] Keys);
    private sealed record KeyEntry(string KeyId, string KeyPath);
}
