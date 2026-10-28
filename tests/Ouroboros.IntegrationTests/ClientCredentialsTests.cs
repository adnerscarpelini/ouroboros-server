using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Ouroboros.Identity.Contracts;

namespace Ouroboros.IntegrationTests;

public sealed class ClientCredentialsTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    internal const string Root = "/api/v1/identity/oauth/token";
    private const string Probe = "/api/v1/identity/client-principal-probe";
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Form_grant_issues_public_client_principal_without_user_session_or_refresh()
    {
        var (id, secret) = await PrepareAsync();
        var sessions = await QueryScalarAsync<long>("SELECT count(*) FROM identity.sessions");
        var refresh = await QueryScalarAsync<long>("SELECT count(*) FROM identity.refresh_tokens");
        using var response = await GrantAsync(Client, id, secret);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(Cancellation);
        Assert.Equal(3, payload.EnumerateObject().Count());
        Assert.Equal("Bearer", payload.GetProperty("token_type").GetString());
        Assert.InRange(payload.GetProperty("expires_in").GetInt64(), 899, 901);
        var token = payload.GetProperty("access_token").GetString()!;
        var jwt = new JsonWebToken(token);
        Assert.Equal("RS256", jwt.Alg);
        Assert.Equal("ouroboros", jwt.Issuer);
        Assert.Equal(["ouroboros-api"], jwt.Audiences);
        Assert.Equal(id.ToString(), jwt.Subject);
        Assert.Equal("client", jwt.GetClaim("principal_type").Value);
        Assert.DoesNotContain(jwt.Claims, claim => claim.Type is "sid" or "role");
        Assert.Equal(sessions, await QueryScalarAsync<long>("SELECT count(*) FROM identity.sessions"));
        Assert.Equal(refresh, await QueryScalarAsync<long>("SELECT count(*) FROM identity.refresh_tokens"));
        Client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        using var accepted = await Client.GetAsync(Probe, Cancellation);
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        using var userEndpoint = await Client.GetAsync("/api/v1/identity/me", Cancellation);
        using var fallback = await Client.GetAsync("/api/v1/integration-probe/claims", Cancellation);
        Assert.Equal(HttpStatusCode.Forbidden, userEndpoint.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, fallback.StatusCode);
        using var admin = Api.CreateAdminClient();
        admin.DefaultRequestHeaders.Authorization = new("Bearer", token);
        using var administrative = await admin.GetAsync("/api/v1/admin/identity/me", Cancellation);
        Assert.Equal(HttpStatusCode.Unauthorized, administrative.StatusCode);
    }

    [Fact]
    public async Task Basic_authentication_is_supported_but_mixed_methods_are_refused()
    {
        var (id, secret) = await PrepareAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, Root)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials" }),
        };
        request.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(id + ":" + secret)));
        using var response = await Client.SendAsync(request, Cancellation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var mixed = new HttpRequestMessage(HttpMethod.Post, Root) { Content = Form(id, secret) };
        mixed.Headers.Authorization = request.Headers.Authorization;
        using var refused = await Client.SendAsync(mixed, Cancellation);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    [Fact]
    public async Task Wrong_unknown_and_inactive_credentials_share_the_same_generic_error()
    {
        var (id, secret) = await PrepareAsync();
        string? first = null;
        foreach (var pair in new[] { (id, "incorrect-secret"), (Guid.CreateVersion7(), secret), (id, secret) })
        {
            if (pair == (id, secret)) { await ExecuteAsync("UPDATE identity.api_clients SET is_active = false"); }
            using var response = await GrantAsync(Client, pair.Item1, pair.Item2);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>(Cancellation);
            Assert.Equal("invalid_client", payload.GetProperty("error").GetString());
            var title = payload.GetProperty("title").GetString();
            first ??= title;
            Assert.Equal(first, title);
            Assert.DoesNotContain(secret, payload.GetRawText());
        }
    }

    [Theory]
    [InlineData("password", "", "unsupported_grant_type")]
    [InlineData("client_credentials", "banking.write", "invalid_scope")]
    public async Task Unsupported_grant_or_scope_is_not_silently_accepted(string grant, string scope, string error)
    {
        using var response = await Client.PostAsync(Root, new FormUrlEncodedContent(new Dictionary<string, string>
        { ["grant_type"] = grant, ["scope"] = scope }), Cancellation);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(error, (await response.Content.ReadFromJsonAsync<JsonElement>(Cancellation)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Duplicate_parameters_and_json_content_are_rejected()
    {
        var (id, secret) = await PrepareAsync();
        using var duplicate = await Client.PostAsync(Root, new FormUrlEncodedContent(new[]
        { new KeyValuePair<string, string>("grant_type", "client_credentials"), new KeyValuePair<string, string>("grant_type", "password") }), Cancellation);
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        using var json = await Client.PostAsJsonAsync(Root, new { grant_type = "client_credentials", client_id = id, client_secret = secret }, Cancellation);
        // O endpoint sintético de Consumes também pode ser recusado pela autorização de fallback.
        Assert.InRange((int)json.StatusCode, 400, 499);
        Assert.DoesNotContain("access_token", await json.Content.ReadAsStringAsync(Cancellation));
    }

    [Theory]
    [InlineData("role")]
    [InlineData("sid")]
    public async Task Client_principal_cannot_carry_user_identity_claims(string extraClaim)
    {
        var (id, _) = await PrepareAsync();
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "ouroboros",
            Audience = "ouroboros-api",
            Expires = DateTime.UtcNow.AddMinutes(5),
            Subject = new ClaimsIdentity([new Claim("sub", id.ToString()), new Claim("principal_type", "client"),
                new Claim(extraClaim, extraClaim == "role" ? "Admin" : Guid.CreateVersion7().ToString())]),
            SigningCredentials = new SigningCredentials(TestSigningKeys.Key, SecurityAlgorithms.RsaSha256),
        });
        Client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        using var response = await Client.GetAsync(Probe, Cancellation);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Authentication_rate_limit_applies_before_credential_lookup()
    {
        await using var factory = Api.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Identity:RateLimiting:Policies:identity-login:PermitLimit"] = "2", ["Identity:RateLimiting:Policies:identity-login:Window"] = "01:00:00" })));
        using var client = factory.CreateClient();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var refused = await GrantAsync(client, Guid.CreateVersion7(), "invalid-secret");
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }
        using var limited = await GrantAsync(client, Guid.CreateVersion7(), "invalid-secret");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Credential_change_before_row_lock_prevents_issuing_token(bool deactivate)
    {
        var (id, secret) = await PrepareAsync();
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(Cancellation);
        await using var transaction = await connection.BeginTransactionAsync(Cancellation);
        await using (var command = new NpgsqlCommand("SELECT 1 FROM identity.api_clients FOR NO KEY UPDATE", connection, transaction))
        { await command.ExecuteScalarAsync(Cancellation); }
        var pending = GrantAsync(Client, id, secret);
        var waiting = false;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            waiting = await QueryScalarAsync<long>("SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND query LIKE '%FOR UPDATE%'") > 0;
            if (waiting) { break; }
            await Task.Delay(50, Cancellation);
        }
        Assert.True(waiting, "Emissão deve aguardar o lock após validar a credencial inicial.");
        await using (var command = new NpgsqlCommand(deactivate ? "UPDATE identity.api_clients SET is_active = false"
            : "UPDATE identity.api_clients SET secret_hash = decode(repeat('00', 32), 'hex')", connection, transaction))
        { await command.ExecuteNonQueryAsync(Cancellation); }
        await transaction.CommitAsync(Cancellation);
        using var response = await pending;
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("access_token", await response.Content.ReadAsStringAsync(Cancellation));
    }

    [Theory]
    [InlineData("duplicate-kind")]
    [InlineData("duplicate-subject")]
    [InlineData("empty-subject")]
    [InlineData("unknown-kind")]
    public async Task Malformed_client_principal_is_refused(string variant)
    {
        var (id, _) = await PrepareAsync();
        var claims = new List<Claim>
        {
            new("sub", variant == "empty-subject" ? Guid.Empty.ToString() : id.ToString()),
            new("principal_type", variant == "unknown-kind" ? "other" : "client"),
        };
        if (variant == "duplicate-kind") { claims.Add(new("principal_type", "client")); }
        if (variant == "duplicate-subject") { claims.Add(new("sub", Guid.CreateVersion7().ToString())); }
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "ouroboros",
            Audience = "ouroboros-api",
            Expires = DateTime.UtcNow.AddMinutes(5),
            Subject = new ClaimsIdentity(claims),
            SigningCredentials = new SigningCredentials(TestSigningKeys.Key, SecurityAlgorithms.RsaSha256),
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, Probe);
        request.Headers.Authorization = new("Bearer", token);
        using var response = await Client.SendAsync(request, Cancellation);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Issued_token_is_checked_against_current_client_state_on_every_request()
    {
        var (id, secret) = await PrepareAsync();
        using var grant = await GrantAsync(Client, id, secret);
        var token = (await grant.Content.ReadFromJsonAsync<JsonElement>(Cancellation)).GetProperty("access_token").GetString();
        Client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        using (var active = await Client.GetAsync(Probe, Cancellation)) { Assert.Equal(HttpStatusCode.NoContent, active.StatusCode); }
        await ExecuteAsync("UPDATE identity.api_clients SET is_active = false");
        using var inactive = await Client.GetAsync(Probe, Cancellation);
        Assert.Equal(HttpStatusCode.Unauthorized, inactive.StatusCode);
    }

    [Fact]
    public async Task Authoritative_validation_timeout_denies_access_and_recovers_without_cached_success()
    {
        var (id, secret) = await PrepareAsync();
        using var grant = await GrantAsync(Client, id, secret);
        var token = (await grant.Content.ReadFromJsonAsync<JsonElement>(Cancellation)).GetProperty("access_token").GetString();
        Client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        using (var active = await Client.GetAsync(Probe, Cancellation)) { Assert.Equal(HttpStatusCode.NoContent, active.StatusCode); }
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(Cancellation);
        await using var transaction = await connection.BeginTransactionAsync(Cancellation);
        await using (var command = new NpgsqlCommand("LOCK TABLE identity.api_clients IN ACCESS EXCLUSIVE MODE", connection, transaction))
        { await command.ExecuteNonQueryAsync(Cancellation); }
        var started = Stopwatch.GetTimestamp();
        using var timedOut = await Client.GetAsync(Probe, Cancellation);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, timedOut.StatusCode);
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5));
        var text = await timedOut.Content.ReadAsStringAsync(Cancellation);
        Assert.DoesNotContain(secret, text);
        Assert.DoesNotContain("Npgsql", text, StringComparison.OrdinalIgnoreCase);
        await transaction.RollbackAsync(Cancellation);
        using var recovered = await Client.GetAsync(Probe, Cancellation);
        Assert.Equal(HttpStatusCode.NoContent, recovered.StatusCode);
    }

    [Fact]
    public async Task Issuance_and_authenticated_use_do_not_log_secret_or_access_token()
    {
        var (id, secret) = await PrepareAsync();
        var logs = new ConcurrentQueue<string>();
        await using var api = Api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<ILoggerProvider>(new ListLoggerProvider(logs))));
        using var client = api.CreateClient();
        using var grant = await GrantAsync(client, id, secret);
        Assert.Equal(HttpStatusCode.OK, grant.StatusCode);
        var token = (await grant.Content.ReadFromJsonAsync<JsonElement>(Cancellation)).GetProperty("access_token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        using var accepted = await client.GetAsync(Probe, Cancellation);
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        using var refused = await GrantAsync(client, id, "incorrect-secret");
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.DoesNotContain(logs, entry => entry.Contains(secret, StringComparison.Ordinal) || entry.Contains(token, StringComparison.Ordinal));
    }

    private async Task<(Guid Id, string Secret)> PrepareAsync()
    {
        await ApiClientTestSupport.PrepareAsync(Api.Services);
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        using var admin = await AdminInvitationTests.AdminAsync(api);
        using var response = await ApiClientRegistrationTests.RegisterAsync(admin);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(Cancellation);
        return (payload.GetProperty("client_id").GetGuid(), payload.GetProperty("client_secret").GetString()!);
    }

    internal static FormUrlEncodedContent Form(Guid id, string secret) => new(new Dictionary<string, string>
    { ["grant_type"] = "client_credentials", ["client_id"] = id.ToString(), ["client_secret"] = secret });
    internal static Task<HttpResponseMessage> GrantAsync(HttpClient client, Guid id, string secret) => client.PostAsync(Root, Form(id, secret), Cancellation);
}

[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/identity/client-principal-probe")]
[Authorize(Policy = IdentityPolicies.ApiClientAccess)]
public sealed class ClientPrincipalProbeController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => NoContent();
}
