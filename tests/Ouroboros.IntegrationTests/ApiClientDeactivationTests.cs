using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Ouroboros.Identity.Domain.Notifications;
using Ouroboros.Identity.Infrastructure.Messaging;
using Ouroboros.Identity.Infrastructure.Persistence;

namespace Ouroboros.IntegrationTests;

public sealed class ApiClientDeactivationTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private const string ValidationPath = "/api/v1/identity/oauth/validation";
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Deactivation_is_audited_and_next_validation_and_grant_refuse_old_credentials()
    {
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        var (id, secret, actor) = await PrepareAsync(api);
        var token = await TokenAsync(id, secret);
        using (var before = await ValidateAsync(Client, token)) { Assert.Equal(HttpStatusCode.NoContent, before.StatusCode); }
        using var admin = await AdminInvitationTests.AdminAsync(api);
        using var response = await DeactivateAsync(admin, id);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(await QueryScalarAsync<bool>("SELECT is_active FROM identity.api_clients"));
        Assert.Equal(actor, await QueryScalarAsync<Guid>("SELECT deactivated_by FROM identity.api_clients"));
        Assert.True(await QueryScalarAsync<bool>("SELECT deactivated_at IS NOT NULL FROM identity.api_clients"));
        using var refused = await ValidateAsync(Client, token);
        using var grant = await ClientCredentialsTests.GrantAsync(Client, id, secret);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, grant.StatusCode);
        var notices = await TestOutbox.ReadAsync(api.Services);
        Assert.Equal(4, notices.Count);
        var hash = Convert.ToHexString(Ouroboros.Identity.Application.Security.OpaqueTokens.Hash(secret));
        Assert.All(notices, notice =>
        {
            Assert.DoesNotContain(secret, notice.Body);
            Assert.DoesNotContain(hash, notice.Body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(token, notice.Body);
        });
        Assert.Equal(2, notices.Count(notice => notice.Subject == "Integrador desativado"));
        Assert.DoesNotContain(notices, notice => notice.To.StartsWith("inactive", StringComparison.Ordinal));
        using var summary = await admin.GetAsync(ApiClientRegistrationTests.Root + "/" + id, Cancellation);
        Assert.Equal(HttpStatusCode.OK, summary.StatusCode);
        var body = await summary.Content.ReadAsStringAsync(Cancellation);
        Assert.DoesNotContain(secret, body);
        Assert.DoesNotContain("secret", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Incorrect_password_does_not_deactivate_or_add_notices()
    {
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        var (id, _, _) = await PrepareAsync(api);
        using var admin = await AdminInvitationTests.AdminAsync(api);
        using var response = await DeactivateAsync(admin, id, "incorrect-password");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertUnchangedAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Customer_or_demoted_admin_cannot_deactivate(bool demoted)
    {
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        var (id, _, actor) = await PrepareAsync(api);
        using var caller = await AdminInvitationTests.AdminAsync(api);
        if (demoted) { await ExecuteAsync("UPDATE identity.users SET role = 'Customer' WHERE email = 'admin@adnerscarpelini.com'"); }
        else { caller.DefaultRequestHeaders.Authorization = new("Bearer", ApiClientTestSupport.Token(actor, "Customer")); }
        using var response = await DeactivateAsync(caller, id);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertUnchangedAsync();
    }

    [Fact]
    public async Task Missing_client_and_anonymous_caller_do_not_change_state()
    {
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        var (id, _, _) = await PrepareAsync(api);
        using var admin = await AdminInvitationTests.AdminAsync(api);
        using var missing = await DeactivateAsync(admin, Guid.CreateVersion7());
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var anonymous = api.CreateClient();
        using var refused = await DeactivateAsync(anonymous, id);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        await AssertUnchangedAsync();
    }

    [Fact]
    public async Task Repeated_deactivation_keeps_original_audit_and_does_not_repeat_notices()
    {
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        var (id, _, actor) = await PrepareAsync(api);
        using var first = await AdminInvitationTests.AdminAsync(api);
        using var second = await AdminInvitationTests.AdminAsync(api, "oi@adnerscarpelini.com");
        using var initial = await DeactivateAsync(first, id);
        Assert.Equal(HttpStatusCode.NoContent, initial.StatusCode);
        var timestamp = await QueryScalarAsync<DateTime>("SELECT deactivated_at FROM identity.api_clients");
        using var repeated = await DeactivateAsync(second, id);
        Assert.Equal(HttpStatusCode.NoContent, repeated.StatusCode);
        Assert.Equal(actor, await QueryScalarAsync<Guid>("SELECT deactivated_by FROM identity.api_clients"));
        Assert.Equal(timestamp, await QueryScalarAsync<DateTime>("SELECT deactivated_at FROM identity.api_clients"));
        Assert.Equal(4L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.email_intents"));
    }

    [Fact]
    public async Task Concurrent_admins_deactivate_once_and_produce_one_notice_set()
    {
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        var (id, _, actor) = await PrepareAsync(api);
        var other = await QueryScalarAsync<Guid>("SELECT external_id FROM identity.users WHERE email = 'oi@adnerscarpelini.com'");
        using var first = await AdminInvitationTests.AdminAsync(api);
        using var second = await AdminInvitationTests.AdminAsync(api, "oi@adnerscarpelini.com");
        var replies = await Task.WhenAll(DeactivateAsync(first, id), DeactivateAsync(second, id));
        try
        {
            Assert.All(replies, response => Assert.Equal(HttpStatusCode.NoContent, response.StatusCode));
            Assert.False(await QueryScalarAsync<bool>("SELECT is_active FROM identity.api_clients"));
            Assert.Contains(await QueryScalarAsync<Guid>("SELECT deactivated_by FROM identity.api_clients"), new[] { actor, other });
            Assert.Equal(4L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.email_intents"));
        }
        finally { foreach (var response in replies) { response.Dispose(); } }
    }

    [Fact]
    public async Task Notice_insert_failure_rolls_back_state_audit_and_revocation()
    {
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        var (id, secret, _) = await PrepareAsync(api);
        var token = await TokenAsync(id, secret);
        await ExecuteAsync("""
            CREATE FUNCTION identity.fail_deactivation_notice() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'induced-deactivation-storage-failure'; END $$;
            CREATE TRIGGER fail_deactivation_notice BEFORE INSERT ON identity.email_intents
            FOR EACH ROW EXECUTE FUNCTION identity.fail_deactivation_notice();
            """);
        using var admin = await AdminInvitationTests.AdminAsync(api);
        using var response = await DeactivateAsync(admin, id);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await AssertUnchangedAsync();
        using var valid = await ValidateAsync(Client, token);
        Assert.Equal(HttpStatusCode.NoContent, valid.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("text/plain")]
    public async Task Authoritative_sql_timeout_returns_sanitized_503_and_recovers_without_success_cache(string? accept)
    {
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        var (id, secret, _) = await PrepareAsync(api);
        var token = await TokenAsync(id, secret);
        using (var before = await ValidateAsync(Client, token)) { Assert.Equal(HttpStatusCode.NoContent, before.StatusCode); }
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(Cancellation);
        await using var transaction = await connection.BeginTransactionAsync(Cancellation);
        await using (var command = new NpgsqlCommand("LOCK TABLE identity.api_clients IN ACCESS EXCLUSIVE MODE", connection, transaction))
        { await command.ExecuteNonQueryAsync(Cancellation); }
        var started = Stopwatch.GetTimestamp();
        using var response = await ValidateAsync(Client, token, accept);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5));
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var text = await response.Content.ReadAsStringAsync(Cancellation);
        Assert.DoesNotContain(secret, text);
        Assert.DoesNotContain(token, text);
        Assert.DoesNotContain("Npgsql", text, StringComparison.OrdinalIgnoreCase);
        using var problem = JsonDocument.Parse(text);
        Assert.Equal(503, problem.RootElement.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(problem.RootElement.GetProperty("traceId").GetString()));
        await transaction.RollbackAsync(Cancellation);
        using var recovered = await ValidateAsync(Client, token);
        Assert.Equal(HttpStatusCode.NoContent, recovered.StatusCode);
    }

    [Fact]
    public async Task Validation_is_client_only_authenticated_and_rate_limited()
    {
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        var (id, secret, actor) = await PrepareAsync(api);
        var token = await TokenAsync(id, secret);
        using (var anonymous = await Client.PostAsync(ValidationPath, null, Cancellation)) { Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode); }
        using (var user = await ValidateAsync(Client, ApiClientTestSupport.Token(actor, "Customer", "ouroboros-api")))
        { Assert.Equal(HttpStatusCode.Forbidden, user.StatusCode); }
        await using var limitedApi = Api.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Identity:RateLimiting:Policies:identity-client-validation:PermitLimit"] = "2",
            ["Identity:RateLimiting:Policies:identity-client-validation:Window"] = "01:00:00",
        })));
        using var limitedClient = limitedApi.CreateClient();
        for (var index = 0; index < 2; index++)
        { using var allowed = await ValidateAsync(limitedClient, token); Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode); }
        using var refused = await ValidateAsync(limitedClient, token);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.True(refused.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task Deactivation_and_validation_logs_never_contain_credentials()
    {
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        var (id, secret, _) = await PrepareAsync(api);
        var token = await TokenAsync(id, secret);
        var logs = new ConcurrentQueue<string>();
        await using var capturedAdmin = api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<ILoggerProvider>(new ListLoggerProvider(logs))));
        await using var capturedPublic = Api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<ILoggerProvider>(new ListLoggerProvider(logs))));
        using var admin = await AdminInvitationTests.AdminAsync(capturedAdmin);
        using var publicClient = capturedPublic.CreateClient();
        using var response = await DeactivateAsync(admin, id);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var rejected = await ValidateAsync(publicClient, token);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        var hash = Convert.ToHexString(Ouroboros.Identity.Application.Security.OpaqueTokens.Hash(secret));
        Assert.DoesNotContain(logs, entry => entry.Contains(secret, StringComparison.Ordinal) || entry.Contains(token, StringComparison.Ordinal)
            || entry.Contains(hash, StringComparison.OrdinalIgnoreCase) || entry.Contains(AdminInvitationTests.Password, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Publication_failure_preserves_inactive_state_audit_and_recoverable_notices()
    {
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        var (id, secret, actor) = await PrepareAsync(api);
        var token = await TokenAsync(id, secret);
        using var admin = await AdminInvitationTests.AdminAsync(api);
        using var response = await DeactivateAsync(admin, id);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var metrics = new EmailOutboxMetrics();
        var environment = new HostApplicationBuilder(new HostApplicationBuilderSettings
        { DisableDefaults = true, EnvironmentName = Environments.Development }).Environment;
        using var publisher = new EmailOutboxPublisher(api.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new EmailPublisherOptions { Host = "127.0.0.1", Port = 1, Password = new string('x', 40), BatchSize = 8 }),
            environment, TimeProvider.System, metrics, NullLogger<EmailOutboxPublisher>.Instance);
        await publisher.RunOnceAsync(Cancellation);
        Assert.False(await QueryScalarAsync<bool>("SELECT is_active FROM identity.api_clients"));
        Assert.Equal(actor, await QueryScalarAsync<Guid>("SELECT deactivated_by FROM identity.api_clients"));
        await using var scope = api.Services.CreateAsyncScope();
        var intents = await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().EmailIntents.AsNoTracking().ToArrayAsync(Cancellation);
        Assert.Equal(4, intents.Length);
        Assert.All(intents, intent =>
        {
            Assert.Equal(EmailIntentState.Pending, intent.State);
            Assert.NotNull(intent.ProtectedEnvelope);
            Assert.True(intent.Attempts > 0);
            Assert.NotNull(intent.NextAttemptAt);
        });
        using var validation = await ValidateAsync(Client, token);
        using var grant = await ClientCredentialsTests.GrantAsync(Client, id, secret);
        Assert.Equal(HttpStatusCode.Unauthorized, validation.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, grant.StatusCode);
        using var repeated = await DeactivateAsync(admin, id);
        Assert.Equal(HttpStatusCode.NoContent, repeated.StatusCode);
        Assert.Equal(4L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.email_intents"));
    }

    private async Task<(Guid Id, string Secret, Guid Actor)> PrepareAsync(BackofficeIntegrationApiFactory api)
    {
        var actor = await ApiClientTestSupport.PrepareAsync(Api.Services);
        using var admin = await AdminInvitationTests.AdminAsync(api);
        using var response = await ApiClientRegistrationTests.RegisterAsync(admin);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Cancellation);
        return (body.GetProperty("client_id").GetGuid(), body.GetProperty("client_secret").GetString()!, actor);
    }

    private async Task<string> TokenAsync(Guid id, string secret)
    {
        using var response = await ClientCredentialsTests.GrantAsync(Client, id, secret);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Cancellation)).GetProperty("access_token").GetString()!;
    }

    private async Task AssertUnchangedAsync()
    {
        Assert.True(await QueryScalarAsync<bool>("SELECT is_active FROM identity.api_clients"));
        Assert.True(await QueryScalarAsync<bool>("SELECT deactivated_by IS NULL AND deactivated_at IS NULL FROM identity.api_clients"));
        Assert.Equal(2L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.email_intents"));
    }

    private static Task<HttpResponseMessage> DeactivateAsync(HttpClient client, Guid id, string password = AdminInvitationTests.Password) =>
        client.PostAsJsonAsync(ApiClientRegistrationTests.Root + "/" + id + "/deactivation", new { CurrentPassword = password }, Cancellation);

    private static Task<HttpResponseMessage> ValidateAsync(HttpClient client, string token, string? accept = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, ValidationPath);
        request.Headers.Authorization = new("Bearer", token);
        if (accept is not null) { request.Headers.Accept.ParseAdd(accept); }
        return SendAndDisposeAsync(client, request);
    }

    private static async Task<HttpResponseMessage> SendAndDisposeAsync(HttpClient client, HttpRequestMessage request)
    {
        using (request) { return await client.SendAsync(request, Cancellation); }
    }
}
