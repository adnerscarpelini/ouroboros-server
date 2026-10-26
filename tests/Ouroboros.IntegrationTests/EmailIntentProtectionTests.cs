using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Ouroboros.Identity.Application.Notifications;
using Ouroboros.Identity.Contracts;
using Ouroboros.Identity.Infrastructure.Messaging;
using Ouroboros.Messaging;
using Ouroboros.Notifications.Infrastructure;

namespace Ouroboros.IntegrationTests;

public sealed class EmailIntentProtectionTests
{
    [Fact]
    public void Rotation_uses_new_public_key_and_retained_private_keys_decrypt_both_generations()
    {
        using var files = new ProtectionFiles();
        using var first = RSA.Create(2048);
        using var second = RSA.Create(2048);
        files.WriteKeys(("first", first));
        var factory = files.Factory();
        var old = Create(factory);
        files.WriteKeys(("first", first), ("second", second));
        var current = Create(factory);
        Assert.Equal("first", old.ProtectionKeyId);
        Assert.Equal("second", current.ProtectionKeyId);
        Assert.Equal(old.SupersessionKey, current.SupersessionKey);
        Assert.NotEqual(old.ExternalId, current.ExternalId);
        var reader = new ProtectedEmailPayloadReader(Options.Create(new NotificationOptions { PrivateKeyRingPath = files.PrivatePath }));
        foreach (var intent in new[] { old, current })
        {
            Assert.True(EnvelopeCodec.TryDecode(intent.ProtectedEnvelope!, intent.Type, intent.Version, "identity", out var message, out _));
            Assert.Equal("secret-link", reader.Read(message!).Text);
            Assert.ThrowsAny<InvalidOperationException>(() => reader.Read(message! with { ExpiresAt = message.ExpiresAt.AddSeconds(1) }));
        }
    }

    [Fact]
    public void Signing_key_reuse_and_private_material_in_producer_manifest_are_refused_without_secret_details()
    {
        using var files = new ProtectionFiles();
        using var signing = RSA.Create();
        signing.ImportParameters(TestSigningKeys.Parameters);
        files.WriteKeys(("signing", signing));
        var error = Assert.Throws<InvalidOperationException>(() => Create(files.Factory()));
        Assert.Equal("Proteção da intenção de e-mail indisponível ou inválida.", error.Message);
        File.WriteAllText(files.PublicPath, "{\"activeKeyId\":\"test\",\"privateKeyPath\":\"private-secret\",\"keys\":[]}");
        Assert.DoesNotContain("private-secret", Assert.Throws<InvalidOperationException>(() => Create(files.Factory())).ToString());
    }

    [Fact]
    public void Managed_backup_purge_removes_only_owned_expired_files_and_is_bounded()
    {
        using var files = new ProtectionFiles();
        File.WriteAllText(Path.Combine(files.DirectoryPath, ".email-intent-backups"), EmailIntentBackupRetention.Marker);
        var now = new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);
        var expired = Path.Combine(files.DirectoryPath, $"email-intents-{now.AddDays(-31):yyyyMMddHHmmss}-{Guid.NewGuid():N}.json");
        var retained = Path.Combine(files.DirectoryPath, $"email-intents-{now:yyyyMMddHHmmss}-{Guid.NewGuid():N}.json");
        var unrelated = Path.Combine(files.DirectoryPath, "legacy-backup.dump");
        foreach (var path in new[] { expired, retained, unrelated }) { File.WriteAllText(path, "test"); }
        EmailIntentBackupRetention.Purge(files.DirectoryPath, now);
        Assert.False(File.Exists(expired));
        Assert.True(File.Exists(retained));
        Assert.True(File.Exists(unrelated));
    }

    private static Ouroboros.Identity.Domain.Notifications.EmailIntent Create(ProtectedEmailIntentFactory factory)
    {
        var now = DateTimeOffset.UtcNow;
        return factory.Create(new EmailMessage("receiver@example.com", "Subject", "secret-link"), "registration", now, now.AddMinutes(15));
    }

    private sealed class SigningKeys : ITokenPublicKeys
    {
        public PublicKeySet Get() => new([new PublicTokenKey("RSA", "sig", "RS256", "test-key",
            Base64UrlEncoder.Encode(TestSigningKeys.Parameters.Modulus!), Base64UrlEncoder.Encode(TestSigningKeys.Parameters.Exponent!))]);
    }

    private sealed class ProtectionFiles : IDisposable
    {
        internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "ouroboros-protection-tests-" + Guid.NewGuid().ToString("N"));
        internal string PublicPath => Path.Combine(DirectoryPath, "public.json");
        internal string PrivatePath => Path.Combine(DirectoryPath, "private.json");
        private string AliasPath => Path.Combine(DirectoryPath, "alias.key");

        internal ProtectionFiles()
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllBytes(AliasPath, RandomNumberGenerator.GetBytes(32));
        }

        internal void WriteKeys(params (string Id, RSA Rsa)[] keys)
        {
            File.WriteAllText(PublicPath, JsonSerializer.Serialize(new
            {
                activeKeyId = keys[^1].Id,
                keys = keys.Select(key => new { keyId = key.Id, n = Base64UrlEncoder.Encode(key.Rsa.ExportParameters(false).Modulus!), e = Base64UrlEncoder.Encode(key.Rsa.ExportParameters(false).Exponent!) }),
            }));
            foreach (var key in keys) { File.WriteAllText(Path.Combine(DirectoryPath, key.Id + ".pem"), key.Rsa.ExportPkcs8PrivateKeyPem()); }
            File.WriteAllText(PrivatePath, JsonSerializer.Serialize(new
            {
                keys = keys.Select(key => new { keyId = key.Id, privateKeyPath = Path.Combine(DirectoryPath, key.Id + ".pem") }),
            }));
        }

        internal ProtectedEmailIntentFactory Factory() => new(Options.Create(new EmailIntentOptions
        { PublicKeyRingPath = PublicPath, SupersessionKeyPath = AliasPath }), new SigningKeys());

        public void Dispose() => Directory.Delete(DirectoryPath, true);
    }
}
