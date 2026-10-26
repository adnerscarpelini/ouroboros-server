using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Ouroboros.Identity.Infrastructure.Security;

namespace Ouroboros.IntegrationTests;

public sealed class TokenKeyRotationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ouroboros-rotation-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly RSA _old = RSA.Create(2048);
    private readonly RSA _next = RSA.Create(2048);
    private readonly TokenKeyRing _ring;

    public TokenKeyRotationTests()
    {
        Directory.CreateDirectory(_directory);
        _ring = new TokenKeyRing(Options.Create(new JwtOptions
        {
            KeyRingPath = Path.Combine(_directory, "ring.json"),
        }), _clock);
        WriteManifest(false);
    }

    [Fact]
    public async Task Rotation_keeps_old_public_key_until_window_ends_without_restarting_consumer()
    {
        var oldToken = Token(_ring.SigningCredentials());
        var source = new PublicKeyHandler(() => JsonSerializer.Serialize(_ring.Get(), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        using var http = new HttpClient(source) { BaseAddress = new Uri("https://identity.test") };
        var cache = new ProofPublicKeyCache(http, _clock);
        await using var consumer = await ConsumerAsync(cache);
        using var client = consumer.GetTestClient();
        Assert.Equal(HttpStatusCode.OK, await Call(client, oldToken));

        WriteManifest(true);
        var nextToken = Token(_ring.SigningCredentials());
        _clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(HttpStatusCode.OK, await Call(client, nextToken));
        Assert.Equal(HttpStatusCode.OK, await Call(client, oldToken));
        Assert.Equal(2, source.Requests);

        source.Unavailable = true;
        Assert.Equal(HttpStatusCode.OK, await Call(client, nextToken));
        _clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(HttpStatusCode.Unauthorized, await Call(client, nextToken));
        source.Unavailable = false;
        _clock.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(HttpStatusCode.Unauthorized, await Call(client, oldToken));
    }

    [Fact]
    public async Task Unknown_kid_refresh_is_throttled_and_unavailable_endpoint_fails_closed()
    {
        var source = new PublicKeyHandler(() => JsonSerializer.Serialize(_ring.Get(), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        using var http = new HttpClient(source) { BaseAddress = new Uri("https://identity.test") };
        await using var consumer = await ConsumerAsync(new ProofPublicKeyCache(http, _clock));
        using var client = consumer.GetTestClient();
        var unknown = Token(new SigningCredentials(new RsaSecurityKey(_next) { KeyId = "unknown" }, "RS256"));
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, await Call(client, unknown));
        }
        Assert.Equal(1, source.Requests);
        _clock.Advance(TimeSpan.FromSeconds(31));
        source.Unavailable = true;
        Assert.Equal(HttpStatusCode.Unauthorized, await Call(client, unknown));
        Assert.Equal(2, source.Requests);
    }

    [Theory]
    [InlineData("integration", "ouroboros", "ouroboros-api", "RS256")]
    [InlineData("service", "ouroboros", "ouroboros-api", "RS256")]
    [InlineData("user", "other", "ouroboros-api", "RS256")]
    [InlineData("user", "ouroboros", "other", "RS256")]
    [InlineData("user", "ouroboros", "ouroboros-api", "RS384")]
    public async Task Public_only_consumer_rejects_wrong_principal_issuer_audience_or_algorithm(string kind, string issuer, string audience, string algorithm)
    {
        var source = new PublicKeyHandler(() => JsonSerializer.Serialize(_ring.Get(), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        using var http = new HttpClient(source) { BaseAddress = new Uri("https://identity.test") };
        await using var consumer = await ConsumerAsync(new ProofPublicKeyCache(http, _clock));
        using var client = consumer.GetTestClient();
        var credentials = new SigningCredentials(new RsaSecurityKey(_old) { KeyId = "old" }, algorithm);
        Assert.Equal(HttpStatusCode.Unauthorized, await Call(client, Token(credentials, kind, issuer, audience)));
    }

    [Fact]
    public async Task Hs256_transition_ends_at_absolute_deadline_and_does_not_need_legacy_file_afterwards()
    {
        var secretPath = Path.Combine(_directory, "legacy.key");
        const string Secret = "test-only-legacy-key-of-at-least-thirty-two-bytes";
        File.WriteAllText(secretPath, Secret);
        var start = _clock.GetUtcNow();
        WriteManifest(false, new LegacyKeyWindow(secretPath, start, start.AddMinutes(15)));
        var token = Token(new SigningCredentials(new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(Secret)), "HS256"));
        var handler = new JsonWebTokenHandler();
        var validation = Validation();
        validation.ValidAlgorithms = ["RS256", "HS256"];
        validation.IssuerSigningKeyResolver = (_, jwt, kid, _) => _ring.Resolve(kid, ((JsonWebToken)jwt).Alg);
        Assert.True((await handler.ValidateTokenAsync(token, validation)).IsValid);
        _clock.Advance(TimeSpan.FromMinutes(15));
        File.Delete(secretPath);
        Assert.False((await handler.ValidateTokenAsync(token, validation)).IsValid);
        Assert.Empty(_ring.Resolve(null, "HS256"));
    }

    [Fact]
    public void Too_short_rotation_window_is_rejected_and_jwks_has_only_public_fields()
    {
        WriteManifest(true, retirementWindow: TimeSpan.FromMinutes(15));
        Assert.Throws<InvalidOperationException>(() => _ring.Get());
        WriteManifest(false);
        var json = JsonSerializer.Serialize(_ring.Get());
        Assert.DoesNotContain("PRIVATE", json);
        Assert.DoesNotContain("PrivateKeyPath", json);
        Assert.DoesNotContain("\"D\"", json);
    }

    private void WriteManifest(bool rotated, LegacyKeyWindow? legacy = null, TimeSpan? retirementWindow = null)
    {
        var active = rotated ? _next : _old;
        var privatePath = Path.Combine(_directory, rotated ? "next.pem" : "old.pem");
        File.WriteAllText(privatePath, active.ExportPkcs8PrivateKeyPem());
        var now = _clock.GetUtcNow();
        PublicKeyEntry Entry(RSA rsa, string id, bool retired)
        {
            var parameters = rsa.ExportParameters(false);
            return new PublicKeyEntry(id, Base64UrlEncoder.Encode(parameters.Modulus!), Base64UrlEncoder.Encode(parameters.Exponent!),
                retired ? now : null, retired ? now.Add(retirementWindow ?? TimeSpan.FromMinutes(21)) : null);
        }
        var keys = rotated ? new[] { Entry(_old, "old", true), Entry(_next, "next", false) } : [Entry(_old, "old", false)];
        File.WriteAllText(Path.Combine(_directory, "ring.json"), JsonSerializer.Serialize(new KeyManifest(rotated ? "next" : "old", privatePath, keys, legacy), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private static string Token(SigningCredentials credentials, string kind = "user", string issuer = "ouroboros", string audience = "ouroboros-api") =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Expires = DateTime.UtcNow.AddMinutes(15),
            SigningCredentials = credentials,
            Subject = new ClaimsIdentity([new Claim("principal_type", kind), new Claim("sub", Guid.NewGuid().ToString()),
                new Claim("sid", Guid.NewGuid().ToString()), new Claim("role", "Customer")]),
        });

    internal static TokenValidationParameters Validation() => new()
    {
        ValidIssuer = "ouroboros",
        ValidAudience = "ouroboros-api",
        ValidAlgorithms = ["RS256"],
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        RequireSignedTokens = true,
        RequireExpirationTime = true,
        TryAllIssuerSigningKeys = false,
        ClockSkew = TimeSpan.FromSeconds(30),
    };

    // Processo lógico consumidor: a única dependência de chaves é HTTP público; não recebe TokenKeyRing/PEM.
    private static async Task<WebApplication> ConsumerAsync(ProofPublicKeyCache keys)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        app.Run(async context =>
    {
        var token = context.Request.Headers.Authorization.ToString().Replace("Bearer ", "", StringComparison.Ordinal);
        try
        {
            var jwt = new JsonWebToken(token);
            var validation = Validation();
            validation.IssuerSigningKeys = await keys.GetAsync(jwt.Kid);
            var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, validation);
            context.Response.StatusCode = result.IsValid && result.ClaimsIdentity.FindFirst("principal_type")?.Value == "user" ? 200 : 401;
        }
        catch (Exception exception) when (exception is HttpRequestException or ArgumentException or JsonException)
        {
            context.Response.StatusCode = 401;
        }
    });
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static async Task<HttpStatusCode> Call(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/private");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    public void Dispose()
    {
        _old.Dispose(); _next.Dispose();
        Directory.Delete(_directory, true);
    }

    private sealed class PublicKeyHandler(Func<string> json) : HttpMessageHandler
    {
        public bool Unavailable { get; set; }
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(Unavailable ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            { Content = new StringContent(Unavailable ? "" : json()) });
        }
    }

    private sealed class ProofPublicKeyCache(HttpClient client, TimeProvider clock)
    {
        private IReadOnlyList<SecurityKey> _keys = [];
        private DateTimeOffset _expires;
        private DateTimeOffset _nextRefresh;
        private readonly SemaphoreSlim _gate = new(1, 1);

        public async Task<IReadOnlyList<SecurityKey>> GetAsync(string kid)
        {
            await _gate.WaitAsync(TestContext.Current.CancellationToken);
            try
            {
                var now = clock.GetUtcNow();
                if ((now >= _expires || !_keys.Any(key => key.KeyId == kid)) && now >= _nextRefresh)
                {
                    _nextRefresh = now.AddSeconds(30);
                    using var response = await client.GetAsync("/api/v1/identity/keys", TestContext.Current.CancellationToken);
                    response.EnsureSuccessStatusCode();
                    var set = new JsonWebKeySet(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
                    _keys = set.GetSigningKeys().ToArray();
                    _expires = now.AddMinutes(5);
                }
                return now < _expires ? _keys.Where(key => key.KeyId == kid).ToArray() : [];
            }
            finally { _gate.Release(); }
        }
    }
}
