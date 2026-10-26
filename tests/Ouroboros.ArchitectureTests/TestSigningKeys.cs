using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace Ouroboros.ArchitectureTests;

// Chaves efêmeras geradas durante o teste; nenhum PEM é versionado.
internal static class TestSigningKeys
{
    internal static readonly RSAParameters Parameters;
    internal static readonly string ManifestPath;
    internal static readonly string EmailPublicKeyRingPath;
    internal static readonly string EmailPrivateKeyRingPath;
    internal static readonly string SupersessionKeyPath;

    static TestSigningKeys()
    {
        using var rsa = RSA.Create(2048);
        Parameters = rsa.ExportParameters(true);
        var directory = Path.Combine(Path.GetTempPath(), "ouroboros-jwt-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var privatePath = Path.Combine(directory, "key.pem");
        File.WriteAllText(privatePath, rsa.ExportPkcs8PrivateKeyPem());
        ManifestPath = Path.Combine(directory, "ring.json");
        File.WriteAllText(ManifestPath, JsonSerializer.Serialize(new
        {
            activeKid = "test-key",
            privateKeyPath = privatePath,
            keys = new[] { new { kid = "test-key", n = Base64UrlEncoder.Encode(Parameters.Modulus!), e = Base64UrlEncoder.Encode(Parameters.Exponent!) } },
        }));
        using var encryption = RSA.Create(3072);
        var encryptionParameters = encryption.ExportParameters(false);
        var encryptionPath = Path.Combine(directory, "notifications.pem");
        File.WriteAllText(encryptionPath, encryption.ExportPkcs8PrivateKeyPem());
        EmailPublicKeyRingPath = Path.Combine(directory, "notifications-public.json");
        EmailPrivateKeyRingPath = Path.Combine(directory, "notifications-private.json");
        SupersessionKeyPath = Path.Combine(directory, "supersession.key");
        File.WriteAllBytes(SupersessionKeyPath, RandomNumberGenerator.GetBytes(32));
        File.WriteAllText(EmailPublicKeyRingPath, JsonSerializer.Serialize(new
        {
            activeKeyId = "test-notifications-key",
            keys = new[] { new { keyId = "test-notifications-key",
                n = Base64UrlEncoder.Encode(encryptionParameters.Modulus!), e = Base64UrlEncoder.Encode(encryptionParameters.Exponent!) } },
        }));
        File.WriteAllText(EmailPrivateKeyRingPath, JsonSerializer.Serialize(new
        {
            keys = new[] { new { keyId = "test-notifications-key", privateKeyPath = encryptionPath } },
        }));
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Directory.Delete(directory, recursive: true);
    }

    internal static SecurityKey Key => new RsaSecurityKey(Parameters) { KeyId = "test-key" };
}
