using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Ouroboros.Identity.Contracts;

namespace Ouroboros.Identity.Infrastructure.Security;

// O manifesto é substituído atomicamente pelo operador; cada operação lê uma única fotografia.
// Somente este adaptador do emissor abre a chave privada. JWKS devolve uma projeção pública fechada.
internal sealed class TokenKeyRing(IOptions<JwtOptions> options, TimeProvider timeProvider) : ITokenPublicKeys
{
    public const int CacheSeconds = 300;
    public const int RefreshSeconds = 30;

    internal KeyManifest Read()
    {
        try
        {
            var path = options.Value.KeyRingPath;
            if (!Path.IsPathFullyQualified(path) || new FileInfo(path).Length > 65_536)
            {
                throw new InvalidOperationException();
            }

            var manifest = JsonSerializer.Deserialize<KeyManifest>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidOperationException();
            var minimumWindow = options.Value.AccessTokenLifetime + TimeSpan.FromSeconds(30 + CacheSeconds);
            if (manifest.Keys is null || manifest.Keys.Length is < 1 or > 16
                || manifest.Keys.Any(key => key is null)
                || manifest.Keys.Select(key => key.Kid).Distinct(StringComparer.Ordinal).Count() != manifest.Keys.Length)
            {
                throw new InvalidOperationException();
            }

            foreach (var key in manifest.Keys)
            {
                if (string.IsNullOrWhiteSpace(key.Kid) || key.Kid.Length > 128
                    || string.IsNullOrWhiteSpace(key.N) || string.IsNullOrWhiteSpace(key.E)
                    || key.RetiredAt is { } retired && (key.AcceptUntil is null || key.AcceptUntil < retired + minimumWindow)
                    || key.RetiredAt is null && key.AcceptUntil is not null)
                {
                    throw new InvalidOperationException();
                }

                using var rsa = RSA.Create();
                rsa.ImportParameters(new RSAParameters { Modulus = Base64UrlEncoder.DecodeBytes(key.N), Exponent = Base64UrlEncoder.DecodeBytes(key.E) });
                if (rsa.KeySize < 2048) { throw new InvalidOperationException(); }
            }

            var active = manifest.Keys.Single(key => key.Kid == manifest.ActiveKid);
            if (active.RetiredAt is not null || string.IsNullOrWhiteSpace(manifest.PrivateKeyPath) || !Path.IsPathFullyQualified(manifest.PrivateKeyPath))
            {
                throw new InvalidOperationException();
            }

            if (manifest.Legacy is { } legacy
                && (legacy.EndsAt <= legacy.StartsAt
                    || legacy.EndsAt - legacy.StartsAt > options.Value.AccessTokenLifetime
                    || string.IsNullOrWhiteSpace(legacy.KeyPath) || !Path.IsPathFullyQualified(legacy.KeyPath)))
            {
                throw new InvalidOperationException();
            }

            return manifest;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
            or CryptographicException or FormatException or InvalidOperationException or ArgumentException)
        {
            // Não propagar caminhos, PEM, JSON ou detalhes criptográficos a logs/exceções.
            throw new InvalidOperationException("Configuração de chaves de assinatura indisponível ou inválida.");
        }
    }

    internal SigningCredentials SigningCredentials()
    {
        var manifest = Read();
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(File.ReadAllText(manifest.PrivateKeyPath));
            var publicKey = rsa.ExportParameters(false);
            var active = manifest.Keys.Single(key => key.Kid == manifest.ActiveKid);
            if (rsa.KeySize < 2048 || Base64UrlEncoder.Encode(publicKey.Modulus!) != active.N
                || Base64UrlEncoder.Encode(publicKey.Exponent!) != active.E)
            {
                throw new CryptographicException();
            }

            return new SigningCredentials(new RsaSecurityKey(rsa.ExportParameters(true)) { KeyId = active.Kid }, SecurityAlgorithms.RsaSha256);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
        {
            throw new InvalidOperationException("Chave de emissão indisponível ou inválida.");
        }
    }

    internal IEnumerable<SecurityKey> Resolve(string? kid, string algorithm)
    {
        var manifest = Read();
        var now = timeProvider.GetUtcNow();
        if (algorithm == SecurityAlgorithms.RsaSha256 && !string.IsNullOrWhiteSpace(kid))
        {
            return manifest.Keys.Where(key => key.Kid == kid && (key.AcceptUntil is null || now < key.AcceptUntil))
                .Select(key => (SecurityKey)new RsaSecurityKey(new RSAParameters
                {
                    Modulus = Base64UrlEncoder.DecodeBytes(key.N),
                    Exponent = Base64UrlEncoder.DecodeBytes(key.E),
                })
                { KeyId = key.Kid }).ToArray();
        }

        if (algorithm == SecurityAlgorithms.HmacSha256 && manifest.Legacy is { } legacy
            && now >= legacy.StartsAt && now < legacy.EndsAt)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(File.ReadAllText(legacy.KeyPath));
                if (bytes.Length >= 32) { return [new SymmetricSecurityKey(bytes)]; }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException("Chave de transição indisponível.");
            }
        }

        return [];
    }

    public PublicKeySet Get()
    {
        var now = timeProvider.GetUtcNow();
        return new PublicKeySet(Read().Keys.Where(key => key.AcceptUntil is null || now < key.AcceptUntil)
            .Select(key => new PublicTokenKey("RSA", "sig", "RS256", key.Kid, key.N, key.E)).ToArray());
    }
}

internal sealed record KeyManifest(string ActiveKid, string PrivateKeyPath, PublicKeyEntry[] Keys, LegacyKeyWindow? Legacy);
internal sealed record PublicKeyEntry(string Kid, string N, string E, DateTimeOffset? RetiredAt, DateTimeOffset? AcceptUntil);
internal sealed record LegacyKeyWindow(string KeyPath, DateTimeOffset StartsAt, DateTimeOffset EndsAt);
