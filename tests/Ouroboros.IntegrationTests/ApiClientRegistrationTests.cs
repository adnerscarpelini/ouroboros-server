using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Ouroboros.Identity.Application.Security;
using Ouroboros.Identity.Domain.Users;
using Ouroboros.Identity.Infrastructure.Persistence;

namespace Ouroboros.IntegrationTests;

public sealed class ApiClientRegistrationTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    internal const string Root = "/api/v1/admin/identity/api-clients";
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Registration_returns_secret_once_and_persists_only_hash_with_audited_notices()
    {
        var actor = await ApiClientTestSupport.PrepareAsync(Api.Services);
        var logs = new ConcurrentQueue<string>();
        await using var baseApi = new BackofficeIntegrationApiFactory(ConnectionString);
        await using var api = baseApi.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<ILoggerProvider>(new ListLoggerProvider(logs))));
        using var admin = await AdminInvitationTests.AdminAsync(api);
        using var response = await RegisterAsync(admin);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(Cancellation);
        var id = payload.GetProperty("client_id").GetGuid();
        var secret = payload.GetProperty("client_secret").GetString()!;
        Assert.Equal(32, Base64Url.DecodeFromChars(secret).Length);
        var hash = Convert.ToHexString(OpaqueTokens.Hash(secret));
        Assert.Equal(hash.ToLowerInvariant(), await QueryScalarAsync<string>("SELECT encode(secret_hash, 'hex') FROM identity.api_clients"));
        Assert.Equal(actor, await QueryScalarAsync<Guid>("SELECT created_by FROM identity.api_clients"));
        Assert.Equal(id, await QueryScalarAsync<Guid>("SELECT external_id FROM identity.api_clients"));
        Assert.True(await QueryScalarAsync<bool>("SELECT is_active FROM identity.api_clients"));
        using var query = await admin.GetAsync(response.Headers.Location, Cancellation);
        Assert.Equal(HttpStatusCode.OK, query.StatusCode);
        var body = await query.Content.ReadAsStringAsync(Cancellation);
        Assert.DoesNotContain("secret", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secret, body);
        var notices = await TestOutbox.ReadAsync(api.Services);
        Assert.Equal(2, notices.Count);
        Assert.All(notices, notice =>
        {
            Assert.Contains(id.ToString(), notice.Body);
            Assert.DoesNotContain(secret, notice.Body);
            Assert.DoesNotContain(hash, notice.Body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(AdminInvitationTests.Password, notice.Body);
        });
        Assert.DoesNotContain(notices, notice => notice.To.StartsWith("inactive"));
        Assert.DoesNotContain(logs, entry => entry.Contains(secret, StringComparison.Ordinal)
            || entry.Contains(hash, StringComparison.OrdinalIgnoreCase) || entry.Contains(AdminInvitationTests.Password, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Incorrect_password_does_not_create_client_or_intents()
    {
        await ApiClientTestSupport.PrepareAsync(Api.Services);
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        using var admin = await AdminInvitationTests.AdminAsync(api);
        using var response = await RegisterAsync(admin, "incorrect-password");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertEmptyAsync();
    }

    [Fact]
    public async Task Customer_principal_with_admin_audience_is_forbidden()
    {
        var actor = await ApiClientTestSupport.PrepareAsync(Api.Services);
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", ApiClientTestSupport.Token(actor, "Customer"));
        using var response = await RegisterAsync(client);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertEmptyAsync();
    }

    [Fact]
    public async Task Demoted_actor_cannot_register_or_query_with_old_admin_token()
    {
        await ApiClientTestSupport.PrepareAsync(Api.Services);
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        using var client = await AdminInvitationTests.AdminAsync(api);
        using var created = await RegisterAsync(client);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Cancellation)).GetProperty("client_id").GetGuid();
        await ExecuteAsync("UPDATE identity.users SET role = 'Customer' WHERE email = 'admin@adnerscarpelini.com'");
        using var refused = await RegisterAsync(client);
        using var query = await client.GetAsync(Root + "/" + id, Cancellation);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, query.StatusCode);
        Assert.Equal(1L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.api_clients"));
        Assert.Equal(2L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.email_intents"));
    }

    [Fact]
    public async Task Notice_persistence_failure_rolls_back_client()
    {
        await ApiClientTestSupport.PrepareAsync(Api.Services);
        await ExecuteAsync("""
            CREATE FUNCTION identity.fail_client_notice() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'induced-client-notice-failure'; END $$;
            CREATE TRIGGER fail_client_notice BEFORE INSERT ON identity.email_intents
            FOR EACH ROW EXECUTE FUNCTION identity.fail_client_notice();
            """);
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        using var client = await AdminInvitationTests.AdminAsync(api);
        using var response = await RegisterAsync(client);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await AssertEmptyAsync();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("invalid\0name")]
    public async Task Invalid_name_returns_400_without_writes(string name)
    {
        await ApiClientTestSupport.PrepareAsync(Api.Services);
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        using var admin = await AdminInvitationTests.AdminAsync(api);
        using var response = await admin.PostAsJsonAsync(Root, new { Name = name, Description = "Teste", CurrentPassword = AdminInvitationTests.Password }, Cancellation);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertEmptyAsync();
    }

    internal static Task<HttpResponseMessage> RegisterAsync(HttpClient client, string password = AdminInvitationTests.Password) =>
        client.PostAsJsonAsync(Root, new { Name = "Integrador de teste", Description = "Integração autorizada", CurrentPassword = password }, Cancellation);

    [Fact]
    public async Task Readiness_requires_the_new_owner_tables_and_recovers_when_schema_is_available()
    {
        using (var before = await Client.GetAsync("/health/ready", Cancellation)) { Assert.Equal(HttpStatusCode.OK, before.StatusCode); }
        await ExecuteAsync("ALTER TABLE identity.api_clients RENAME TO api_clients_unavailable");
        try
        {
            using var missing = await Client.GetAsync("/health/ready", Cancellation);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, missing.StatusCode);
            Assert.Equal("{\"status\":\"Unhealthy\"}", await missing.Content.ReadAsStringAsync(Cancellation));
        }
        finally { await ExecuteAsync("ALTER TABLE identity.api_clients_unavailable RENAME TO api_clients"); }
        using var recovered = await Client.GetAsync("/health/ready", Cancellation);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    [Theory]
    [InlineData(100, 1000, HttpStatusCode.Created)]
    [InlineData(101, 10, HttpStatusCode.BadRequest)]
    [InlineData(1, 1001, HttpStatusCode.BadRequest)]
    public async Task Name_and_description_length_boundaries_are_enforced(int nameLength, int descriptionLength, HttpStatusCode expected)
    {
        await ApiClientTestSupport.PrepareAsync(Api.Services);
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        using var admin = await AdminInvitationTests.AdminAsync(api);
        using var response = await admin.PostAsJsonAsync(Root, new
        {
            Name = new string('n', nameLength),
            Description = new string('d', descriptionLength),
            CurrentPassword = AdminInvitationTests.Password,
        }, Cancellation);
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.BadRequest) { await AssertEmptyAsync(); }
        else
        {
            Assert.Equal(nameLength, await QueryScalarAsync<int>("SELECT length(name) FROM identity.api_clients"));
            Assert.Equal(descriptionLength, await QueryScalarAsync<int>("SELECT length(description) FROM identity.api_clients"));
        }
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Created)]
    [InlineData("invalid\0description", HttpStatusCode.BadRequest)]
    public async Task Optional_description_and_invalid_storage_character_have_defined_results(string? description, HttpStatusCode expected)
    {
        await ApiClientTestSupport.PrepareAsync(Api.Services);
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        using var admin = await AdminInvitationTests.AdminAsync(api);
        using var response = await admin.PostAsJsonAsync(Root, new { Name = "Nome", Description = description, CurrentPassword = AdminInvitationTests.Password }, Cancellation);
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.BadRequest) { await AssertEmptyAsync(); }
        else { Assert.Equal("", await QueryScalarAsync<string>("SELECT description FROM identity.api_clients")); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reauthentication_rechecks_password_and_role_after_waiting_for_actor_lock(bool changeRole)
    {
        await ApiClientTestSupport.PrepareAsync(Api.Services);
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        using var admin = await AdminInvitationTests.AdminAsync(api);
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(Cancellation);
        await using var transaction = await connection.BeginTransactionAsync(Cancellation);
        await using (var command = new NpgsqlCommand("SELECT 1 FROM identity.users WHERE email = 'admin@adnerscarpelini.com' FOR NO KEY UPDATE", connection, transaction))
        { await command.ExecuteScalarAsync(Cancellation); }
        var pending = RegisterAsync(admin);
        var waiting = false;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            waiting = await QueryScalarAsync<long>("SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND query LIKE '%FOR NO KEY UPDATE%'") > 0;
            if (waiting) { break; }
            await Task.Delay(50, Cancellation);
        }
        Assert.True(waiting, "Cadastro deve aguardar o lock depois de verificar a senha.");
        await using (var command = new NpgsqlCommand(changeRole
            ? "UPDATE identity.users SET role = 'Customer' WHERE email = 'admin@adnerscarpelini.com'"
            : "UPDATE identity.users SET password_hash = 'changed-before-lock' WHERE email = 'admin@adnerscarpelini.com'", connection, transaction))
        { await command.ExecuteNonQueryAsync(Cancellation); }
        await transaction.CommitAsync(Cancellation);
        using var response = await pending;
        Assert.Equal(changeRole ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertEmptyAsync();
    }

    private async Task AssertEmptyAsync()
    {
        Assert.Equal(0L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.api_clients"));
        Assert.Equal(0L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.email_intents"));
    }
}

internal static class ApiClientTestSupport
{
    internal static async Task<Guid> PrepareAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var hash = scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(AdminInvitationTests.Password);
        var actor = User.Create("admin@adnerscarpelini.com", hash, UserRole.Admin);
        context.Users.AddRange(actor, User.Create("oi@adnerscarpelini.com", hash, UserRole.Admin), User.Create("inactive@adnerscarpelini.com", hash, UserRole.Admin));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await context.Database.ExecuteSqlRawAsync("UPDATE identity.users SET is_active = false WHERE email = 'inactive@adnerscarpelini.com'", TestContext.Current.CancellationToken);
        return actor.ExternalId;
    }

    internal static string Token(Guid actor, string role = "Admin", string audience = "ouroboros-backoffice") =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "ouroboros",
            Audience = audience,
            Expires = DateTime.UtcNow.AddMinutes(5),
            Subject = new ClaimsIdentity([new Claim("sub", actor.ToString()), new Claim("role", role), new Claim("principal_type", "user"), new Claim("sid", Guid.CreateVersion7().ToString())]),
            SigningCredentials = new SigningCredentials(TestSigningKeys.Key, SecurityAlgorithms.RsaSha256),
        });
}
