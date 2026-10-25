using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace Ouroboros.IntegrationTests;

// Chaves efêmeras geradas durante o teste; nenhum PEM é versionado.
internal static class TestSigningKeys
{
    internal static readonly RSAParameters Parameters;
    internal static readonly string ManifestPath;

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
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Directory.Delete(directory, recursive: true);
    }

    internal static SecurityKey Key => new RsaSecurityKey(Parameters) { KeyId = "test-key" };
}
