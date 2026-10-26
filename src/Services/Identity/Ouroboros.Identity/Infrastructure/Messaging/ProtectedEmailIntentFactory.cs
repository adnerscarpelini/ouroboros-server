using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Ouroboros.Identity.Application.Notifications;
using Ouroboros.Identity.Contracts;
using Ouroboros.Identity.Domain.Notifications;
using Ouroboros.Messaging;
using Ouroboros.Messaging.Contracts;

namespace Ouroboros.Identity.Infrastructure.Messaging;

internal sealed class ProtectedEmailIntentFactory(IOptions<EmailIntentOptions> options, ITokenPublicKeys signingKeys) : IEmailIntentFactory
{
    public EmailIntent Create(EmailMessage content, string purpose, DateTimeOffset now, DateTimeOffset expiresAt)
    {
        byte[]? key = null;
        byte[]? substitutionSecret = null;
        byte[]? plaintext = null;
        try
        {
            var settings = options.Value;
            if (new FileInfo(settings.PublicKeyRingPath).Length > 65_536 || new FileInfo(settings.SupersessionKeyPath).Length != 32)
            { throw new InvalidOperationException(); }
            using var document = JsonDocument.Parse(File.ReadAllText(settings.PublicKeyRingPath));
            var root = document.RootElement;
            if (root.EnumerateObject().Any(property => property.Name is not ("activeKeyId" or "keys"))) { throw new InvalidOperationException(); }
            var activeId = root.GetProperty("activeKeyId").GetString();
            var entries = root.GetProperty("keys").EnumerateArray().ToArray();
            if (entries.Length is < 1 or > 16 || entries.Select(entry => entry.GetProperty("keyId").GetString()).Distinct(StringComparer.Ordinal).Count() != entries.Length)
            { throw new InvalidOperationException(); }
            foreach (var entry in entries)
            {
                if (entry.EnumerateObject().Any(property => property.Name is not ("keyId" or "n" or "e"))) { throw new InvalidOperationException(); }
            }
            var active = entries.Single(entry => entry.GetProperty("keyId").GetString() == activeId);
            var n = active.GetProperty("n").GetString()!;
            var e = active.GetProperty("e").GetString()!;
            var modulus = Base64UrlEncoder.DecodeBytes(n);
            var exponent = Base64UrlEncoder.DecodeBytes(e);
            if (string.IsNullOrWhiteSpace(activeId) || activeId.Length > 128 || signingKeys.Get().Keys.Any(signing =>
                Base64UrlEncoder.DecodeBytes(signing.N).AsSpan().SequenceEqual(modulus)
                && Base64UrlEncoder.DecodeBytes(signing.E).AsSpan().SequenceEqual(exponent)))
            { throw new InvalidOperationException(); }
            using var rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters { Modulus = modulus, Exponent = exponent });
            if (rsa.KeySize is < 2048 or > 8192) { throw new CryptographicException(); }
            substitutionSecret = File.ReadAllBytes(settings.SupersessionKeyPath);
            if (substitutionSecret.Length != 32) { throw new InvalidOperationException(); }
            var substitution = Convert.ToHexString(HMACSHA256.HashData(substitutionSecret, Encoding.UTF8.GetBytes(purpose + ":" + content.To)));
            var activity = Activity.Current is { IdFormat: ActivityIdFormat.W3C, Id: not null } current ? current : null;
            var traceId = activity?.TraceId ?? ActivityTraceId.CreateRandom();
            var traceparent = activity?.Id ?? $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01";
            var intent = EmailIntent.Create(now, expiresAt, substitution, traceparent, activity?.TraceStateString);
            var payload = new ProtectedMessagePayload(EnvelopeCodec.ProtectionAlgorithm, activeId, "", "", "", "");
            // PostgreSQL persiste microssegundos; o envelope não pode prolongar o token por frações descartadas.
            var occurred = new DateTimeOffset(now.UtcTicks - now.UtcTicks % 10, TimeSpan.Zero);
            var deadline = new DateTimeOffset(expiresAt.UtcTicks - expiresAt.UtcTicks % 10, TimeSpan.Zero);
            var message = new MessageEnvelope(intent.ExternalId, intent.Type, intent.Version, occurred, deadline, "identity",
                Guid.ParseExact(traceId.ToHexString(), "N"), Guid.CreateVersion7(), substitution, traceparent, activity?.TraceStateString, payload);
            plaintext = JsonSerializer.SerializeToUtf8Bytes(new { recipient = content.To, subject = content.Subject, text = content.Body, purpose });
            if (plaintext.Length > EnvelopeCodec.MaximumPayloadBytes) { throw new InvalidOperationException(); }
            key = RandomNumberGenerator.GetBytes(32);
            var nonce = RandomNumberGenerator.GetBytes(12);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, EnvelopeCodec.AuthenticationData(message));
            message = message with
            {
                Payload = payload with
                {
                    WrappedKey = Convert.ToBase64String(rsa.Encrypt(key, RSAEncryptionPadding.OaepSHA256)),
                    Nonce = Convert.ToBase64String(nonce),
                    Tag = Convert.ToBase64String(tag),
                    Ciphertext = Convert.ToBase64String(ciphertext),
                }
            };
            intent.Protect(EnvelopeCodec.Encode(message), activeId);
            return intent;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException
            or CryptographicException or ArgumentException or InvalidOperationException or FormatException)
        {
            throw new InvalidOperationException("Proteção da intenção de e-mail indisponível ou inválida.");
        }
        finally
        {
            if (key is not null) { CryptographicOperations.ZeroMemory(key); }
            if (substitutionSecret is not null) { CryptographicOperations.ZeroMemory(substitutionSecret); }
            if (plaintext is not null) { CryptographicOperations.ZeroMemory(plaintext); }
        }
    }
}
