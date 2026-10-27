using System.Net.Mail;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Ouroboros.Messaging;
using Ouroboros.Messaging.Contracts;
using Ouroboros.Notifications.Application;
using Ouroboros.Notifications.Contracts;

namespace Ouroboros.Notifications.Infrastructure;

internal sealed class ProtectedEmailPayloadReader(IOptions<NotificationOptions> options) : IEmailPayloadReader
{
    public EmailContent Read(MessageEnvelope message)
    {
        byte[]? key = null;
        byte[]? plaintext = null;
        try
        {
            using var rsa = LoadPrivateKey(message.Payload.KeyId);
            try { key = rsa.Decrypt(Convert.FromBase64String(message.Payload.WrappedKey), RSAEncryptionPadding.OaepSHA256); }
            catch (CryptographicException) { throw new NotificationDeliveryException(DeliveryFailure.Configuration); }
            if (key.Length != 32) { throw new CryptographicException(); }
            var ciphertext = Convert.FromBase64String(message.Payload.Ciphertext);
            plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(Convert.FromBase64String(message.Payload.Nonce), ciphertext,
                Convert.FromBase64String(message.Payload.Tag), plaintext, EnvelopeCodec.AuthenticationData(message));
            var content = JsonSerializer.Deserialize<EmailContent>(plaintext, new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false });
            if (content is null || !Valid(content)) { throw new InvalidOperationException(); }
            return content;
        }
        catch (NotificationDeliveryException) { throw; }
        catch (Exception exception) when (exception is JsonException or CryptographicException or ArgumentException or InvalidOperationException or FormatException)
        { throw new NotificationDeliveryException(DeliveryFailure.InvalidContent); }
        finally
        {
            if (key is not null) { CryptographicOperations.ZeroMemory(key); }
            if (plaintext is not null) { CryptographicOperations.ZeroMemory(plaintext); }
        }
    }

    private RSA LoadPrivateKey(string keyId)
    {
        RSA? rsa = null;
        try
        {
            var ringPath = options.Value.PrivateKeyRingPath;
            if (new FileInfo(ringPath).Length > 65_536) { throw new InvalidOperationException(); }
            var ring = JsonSerializer.Deserialize<PrivateKeyRing>(File.ReadAllText(ringPath), new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidOperationException();
            if (ring.Keys is null || ring.Keys.Length is < 1 or > 16
                || ring.Keys.Any(entry => entry is null || string.IsNullOrWhiteSpace(entry.KeyId) || string.IsNullOrWhiteSpace(entry.PrivateKeyPath))
                || ring.Keys.Select(entry => entry.KeyId).Distinct(StringComparer.Ordinal).Count() != ring.Keys.Length)
            { throw new InvalidOperationException(); }
            var entry = ring.Keys.Single(entry => entry.KeyId == keyId);
            if (!Path.IsPathFullyQualified(entry.PrivateKeyPath) || new FileInfo(entry.PrivateKeyPath).Length > 16_384)
            { throw new InvalidOperationException(); }
            rsa = RSA.Create();
            rsa.ImportFromPem(File.ReadAllText(entry.PrivateKeyPath));
            if (rsa.KeySize < 2048) { throw new CryptographicException(); }
            return rsa;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
            or CryptographicException or ArgumentException or InvalidOperationException or FormatException)
        {
            rsa?.Dispose();
            throw new NotificationDeliveryException(DeliveryFailure.Configuration);
        }
    }

    private static bool Valid(EmailContent content) => content.Recipient is { Length: > 0 and <= 254 }
        && content.Recipient.AsSpan().IndexOfAny('\r', '\n') < 0
        && MailAddress.TryCreate(content.Recipient, out var address) && address.Address == content.Recipient
        && content.Subject is { Length: > 0 and <= 256 } && content.Subject.AsSpan().IndexOfAny('\r', '\n') < 0
        && content.Text is { Length: > 0 } && content.Purpose is "registration" or "password-reset" or "parameter-change";

    private sealed record PrivateKeyRing(PrivateKeyEntry[] Keys);
    private sealed record PrivateKeyEntry(string KeyId, string PrivateKeyPath);
}
