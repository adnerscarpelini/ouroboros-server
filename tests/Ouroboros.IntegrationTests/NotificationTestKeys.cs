using System.Security.Cryptography;
using System.Text.Json;
using Ouroboros.Messaging;
using Ouroboros.Messaging.Contracts;
using Ouroboros.Notifications.Contracts;

namespace Ouroboros.IntegrationTests;

internal sealed class NotificationTestKeys : IDisposable
{
    private readonly RSA _rsa = RSA.Create(3072);
    internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "ouroboros-notifications-" + Guid.NewGuid().ToString("N"));
    internal string RingPath => Path.Combine(DirectoryPath, "private.json");
    internal string EmailsPath => Path.Combine(DirectoryPath, "emails");
    internal string FingerprintRingPath => Path.Combine(DirectoryPath, "fingerprint.json");
    private readonly List<(string Id, string Path)> _fingerprints = [];
    internal const string KeyId = "notifications-test-key";
    internal static readonly EmailContent Content = new("receiver@example.com", "Confirme seu e-mail", "Use http://localhost/confirm?token=private-test-token e a solicitação mais recente.", "registration");

    internal NotificationTestKeys()
    {
        Directory.CreateDirectory(DirectoryPath);
        var path = Path.Combine(DirectoryPath, "key.pem");
        File.WriteAllText(path, _rsa.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(RingPath, JsonSerializer.Serialize(new
        {
            keys = new[] { new { keyId = KeyId, privateKeyPath = path } },
        }));
        RotateFingerprintKey("first");
    }

    internal void RotateFingerprintKey(string keyId)
    {
        var path = Path.Combine(DirectoryPath, keyId + ".key");
        File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(32));
        _fingerprints.Add((keyId, path));
        File.WriteAllText(FingerprintRingPath, JsonSerializer.Serialize(new
        {
            activeKeyId = keyId,
            keys = _fingerprints.Select(entry => new { keyId = entry.Id, keyPath = entry.Path }),
        }));
    }

    internal MessageEnvelope Message(EmailContent? content = null, Func<MessageEnvelope, MessageEnvelope>? changeMetadata = null)
    {
        var metadata = MessageContractTests.Example() with { Payload = MessageContractTests.Example().Payload with { KeyId = KeyId } };
        if (changeMetadata is not null) { metadata = changeMetadata(metadata); }
        var key = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(content ?? Content, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, EnvelopeCodec.AuthenticationData(metadata));
        var wrapped = _rsa.Encrypt(key, RSAEncryptionPadding.OaepSHA256);
        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(plaintext);
        return metadata with
        {
            Payload = new ProtectedMessagePayload(EnvelopeCodec.ProtectionAlgorithm, KeyId,
            Convert.ToBase64String(wrapped), Convert.ToBase64String(nonce), Convert.ToBase64String(tag), Convert.ToBase64String(ciphertext))
        };
    }

    public void Dispose()
    {
        _rsa.Dispose();
        Directory.Delete(DirectoryPath, true);
    }
}
