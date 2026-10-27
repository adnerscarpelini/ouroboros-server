using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Ouroboros.Identity.Application.Parameters;
using Ouroboros.Identity.Application.Security;
using Ouroboros.Identity.Contracts;
using Ouroboros.Identity.Domain.Parameters;
using Ouroboros.Identity.Domain.Users;
using Ouroboros.Identity.Infrastructure.Messaging;
using Ouroboros.Identity.Infrastructure.Persistence;

namespace Ouroboros.IntegrationTests;

public sealed class ParameterChangeTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private const string Root = "/api/v1/admin/settings/parameters";
    private const string Key = "notice:test";
    private const string Password = "uma frase longa para alterar parametros";
    private const string InitialValue = "valor-sensivel-original";
    private const string NewValue = "valor-sensivel-alterado";
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Sensitive_update_is_atomic_audited_immediate_and_notifies_all_active_admins_without_secrets()
    {
        var author = await PrepareAsync();
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        using var client = await AdminAsync(api);
        Assert.Equal(HttpStatusCode.NoContent, (await ChangeAsync(client, 0, Password, NewValue, "Nova descrição")).StatusCode);
        Assert.Equal(1, await QueryScalarAsync<int>("SELECT revision FROM identity.parameters WHERE key = 'notice:test'"));
        Assert.Equal(author, await QueryScalarAsync<Guid>("SELECT updated_by FROM identity.parameters WHERE key = 'notice:test'"));
        Assert.Equal(2L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.parameter_history WHERE parameter_key = 'notice:test'"));
        await using var scope = api.Services.CreateAsyncScope();
        Assert.Equal(NewValue, await scope.ServiceProvider.GetRequiredService<ISystemParameters>().GetAsync(Key, Cancellation));
        var notices = await TestOutbox.ReadAsync(api.Services);
        Assert.Equal(new[] { "admin@adnerscarpelini.com", "other@adnerscarpelini.com" }, notices.Select(notice => notice.To).Order());
        Assert.All(notices, notice =>
        {
            Assert.Contains(Key, notice.Body);
            Assert.Contains(author.ToString(), notice.Body);
            Assert.DoesNotContain(Password, notice.Body);
            Assert.DoesNotContain(InitialValue, notice.Body);
            Assert.DoesNotContain(NewValue, notice.Body);
        });

        // Descrição apenas também é alteração; cada aviso deve sobreviver a alterações subsequentes.
        Assert.Equal(HttpStatusCode.NoContent, (await ChangeAsync(client, 1, Password, null, "Outra descrição")).StatusCode);
        Assert.Equal(4, (await TestOutbox.ReadAsync(api.Services)).Count);
        Assert.Equal(4L, await QueryScalarAsync<long>("SELECT count(DISTINCT supersession_key) FROM identity.email_intents WHERE state = 'Pending'"));
    }

    [Fact]
    public async Task Non_sensitive_update_writes_history_without_email_intent()
    {
        await PrepareAsync(sensitive: false);
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        using var client = await AdminAsync(api);
        Assert.Equal(HttpStatusCode.NoContent, (await ChangeAsync(client, 0, Password, NewValue)).StatusCode);
        Assert.Equal(0L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.email_intents"));
    }

    [Theory]
    [InlineData("wrong", 0, "new", HttpStatusCode.Unauthorized)]
    [InlineData(Password, 1, "new", HttpStatusCode.Conflict)]
    [InlineData(Password, 0, InitialValue, HttpStatusCode.BadRequest)]
    public async Task Invalid_credentials_revision_or_no_change_have_no_effect(string password, int revision, string value, HttpStatusCode expected)
    {
        await PrepareAsync();
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        using var client = await AdminAsync(api);
        Assert.Equal(expected, (await ChangeAsync(client, revision, password, value)).StatusCode);
        await AssertOriginalAsync();
    }

    [Theory]
    [InlineData("texto")]
    [InlineData("100")]
    public async Task Value_must_satisfy_type_and_pattern(string value)
    {
        await PrepareAsync();
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        using var client = await AdminAsync(api);
        using var response = await client.PutAsJsonAsync(Root + "/Identity:Lockout:MaxFailedAttempts",
            new { Revision = 0, CurrentPassword = Password, Value = value }, Cancellation);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("5", await QueryScalarAsync<string>("SELECT value FROM identity.parameters WHERE key = 'Identity:Lockout:MaxFailedAttempts'"));
    }

    [Theory]
    [InlineData("parameter_history")]
    [InlineData("email_intents")]
    public async Task Failure_in_history_or_intent_rolls_back_everything(string table)
    {
        await PrepareAsync();
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        using var client = await AdminAsync(api);
        await ExecuteAsync($"""
            CREATE FUNCTION fail_parameter_write() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'falha de escrita de teste'; END $$;
            CREATE TRIGGER fail_parameter_write BEFORE INSERT ON identity.{table}
            FOR EACH ROW EXECUTE FUNCTION fail_parameter_write();
            """);
        Assert.Equal(HttpStatusCode.InternalServerError, (await ChangeAsync(client, 0, Password, NewValue)).StatusCode);
        await AssertOriginalAsync();
    }

    [Fact]
    public async Task Concurrent_admins_using_same_revision_have_one_winner_and_one_notice_per_recipient()
    {
        await PrepareAsync();
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        using var first = await AdminAsync(api);
        using var second = await AdminAsync(api, "other@adnerscarpelini.com");
        var results = await Task.WhenAll(ChangeAsync(first, 0, Password, "one"), ChangeAsync(second, 0, Password, "two"));
        Assert.Equal(new[] { HttpStatusCode.NoContent, HttpStatusCode.Conflict }, results.Select(result => result.StatusCode).Order());
        Assert.Equal(2L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.parameter_history WHERE parameter_key = 'notice:test'"));
        Assert.Equal(2L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.email_intents"));
    }

    [Fact]
    public async Task Missing_key_anonymous_and_current_non_admin_are_refused()
    {
        await PrepareAsync();
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        using var anonymous = api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await ChangeAsync(anonymous, 0, Password, NewValue)).StatusCode);
        using var client = await AdminAsync(api);
        using var missing = await client.PutAsJsonAsync(Root + "/missing", new { Revision = 0, CurrentPassword = Password, Value = "value" }, Cancellation);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        await ExecuteAsync("UPDATE identity.users SET role = 'Customer' WHERE email = 'admin@adnerscarpelini.com'");
        Assert.Equal(HttpStatusCode.Unauthorized, (await ChangeAsync(client, 0, Password, NewValue)).StatusCode);
        await AssertOriginalAsync();
    }

    [Fact]
    public async Task Broker_failure_after_commit_keeps_change_confirmed_and_intents_recoverable()
    {
        await PrepareAsync();
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        using var client = await AdminAsync(api);
        Assert.Equal(HttpStatusCode.NoContent, (await ChangeAsync(client, 0, Password, NewValue)).StatusCode);
        using var metrics = new EmailOutboxMetrics();
        using var publisher = new EmailOutboxPublisher(api.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new EmailPublisherOptions { Host = "127.0.0.1", Port = 1, Password = new string('x', 64) }),
            new DevelopmentEnvironment(), TimeProvider.System, metrics, NullLogger<EmailOutboxPublisher>.Instance);
        await publisher.RunOnceAsync(Cancellation);
        Assert.Equal(NewValue, await QueryScalarAsync<string>("SELECT value FROM identity.parameters WHERE key = 'notice:test'"));
        Assert.Equal(1, await QueryScalarAsync<int>("SELECT revision FROM identity.parameters WHERE key = 'notice:test'"));
        Assert.Equal(2L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.email_intents WHERE state = 'Pending' AND protected_envelope IS NOT NULL AND attempts = 1"));
    }

    [Fact]
    public async Task Parameter_write_rate_limit_rejects_eleventh_request_without_writes()
    {
        await PrepareAsync();
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        using var client = await AdminAsync(api);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await ChangeAsync(client, 0, "wrong", NewValue)).StatusCode);
        }

        using var response = await ChangeAsync(client, 0, Password, NewValue);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
        await AssertOriginalAsync();
        Assert.Equal(0, await QueryScalarAsync<int>("SELECT failed_login_count FROM identity.users WHERE email = 'admin@adnerscarpelini.com'"));
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("negative-revision")]
    [InlineData("password")]
    [InlineData("changes")]
    public async Task Invalid_request_body_returns_400_without_writes(string invalid)
    {
        await PrepareAsync();
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        using var client = await AdminAsync(api);
        var body = new Dictionary<string, object?> { ["revision"] = 0, ["currentPassword"] = Password, ["value"] = NewValue };
        if (invalid == "revision") { body.Remove("revision"); }
        if (invalid == "negative-revision") { body["revision"] = -1; }
        if (invalid == "password") { body.Remove("currentPassword"); }
        if (invalid == "changes") { body.Remove("value"); }
        using var response = await client.PutAsJsonAsync(Root + "/" + Key, body, Cancellation);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        await AssertOriginalAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reauthentication_rechecks_hash_and_admin_role_after_waiting_for_user_lock(bool changeRole)
    {
        await PrepareAsync();
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        using var client = await AdminAsync(api);
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(Cancellation);
        await using var transaction = await connection.BeginTransactionAsync(Cancellation);
        await using (var command = new NpgsqlCommand("SELECT 1 FROM identity.users WHERE email = 'admin@adnerscarpelini.com' FOR NO KEY UPDATE", connection, transaction))
        {
            await command.ExecuteScalarAsync(Cancellation);
        }

        var pending = ChangeAsync(client, 0, Password, NewValue);
        var waiting = false;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            waiting = await QueryScalarAsync<long>("SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND query LIKE '%FOR NO KEY UPDATE%'") > 0;
            if (waiting) { break; }
            await Task.Delay(50, Cancellation);
        }

        Assert.True(waiting, "A chamada deve ter verificado a senha e estar aguardando o lock do usuário.");
        await using (var command = new NpgsqlCommand(changeRole
            ? "UPDATE identity.users SET role = 'Customer' WHERE email = 'admin@adnerscarpelini.com'"
            : "UPDATE identity.users SET password_hash = 'changed-before-lock' WHERE email = 'admin@adnerscarpelini.com'", connection, transaction))
        {
            await command.ExecuteNonQueryAsync(Cancellation);
        }

        await transaction.CommitAsync(Cancellation);
        Assert.Equal(HttpStatusCode.Unauthorized, (await pending).StatusCode);
        await AssertOriginalAsync();
    }

    [Theory]
    [InlineData("ouroboros-api", "Admin", HttpStatusCode.Unauthorized)]
    [InlineData("ouroboros-backoffice", "Customer", HttpStatusCode.Forbidden)]
    public async Task Wrong_audience_or_customer_role_is_refused_by_service(string audience, string role, HttpStatusCode expected)
    {
        var author = await PrepareAsync();
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        using var client = api.CreateClient();
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "ouroboros",
            Audience = audience,
            Expires = DateTime.UtcNow.AddMinutes(5),
            Subject = new ClaimsIdentity(
            [
                new Claim("sub", author.ToString()),
                new Claim("role", role),
                new Claim("principal_type", "user"),
                new Claim("sid", Guid.CreateVersion7().ToString()),
            ]),
            SigningCredentials = new SigningCredentials(TestSigningKeys.Key, SecurityAlgorithms.RsaSha256),
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(expected, (await ChangeAsync(client, 0, Password, NewValue)).StatusCode);
        await AssertOriginalAsync();
    }

    private async Task<Guid> PrepareAsync(bool sensitive = true)
    {
        await using var scope = Api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var hash = scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(Password);
        var user = User.Create("admin@adnerscarpelini.com", hash, UserRole.Admin);
        db.Users.AddRange(user, User.Create("other@adnerscarpelini.com", hash, UserRole.Admin), User.Create("inactive@adnerscarpelini.com", hash, UserRole.Admin));
        await db.SaveChangesAsync(Cancellation);
        await ExecuteAsync("UPDATE identity.users SET is_active = false WHERE email = 'inactive@adnerscarpelini.com'");
        await scope.ServiceProvider.GetRequiredService<ParameterWriter>().CreateAsync(Key, InitialValue, "Descrição original", ParameterDataType.String, null, sensitive, user.ExternalId, Cancellation);
        return user.ExternalId;
    }

    private async Task AssertOriginalAsync()
    {
        Assert.Equal(InitialValue, await QueryScalarAsync<string>("SELECT value FROM identity.parameters WHERE key = 'notice:test'"));
        Assert.Equal(0, await QueryScalarAsync<int>("SELECT revision FROM identity.parameters WHERE key = 'notice:test'"));
        Assert.Equal(1L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.parameter_history WHERE parameter_key = 'notice:test'"));
        Assert.Equal(0L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.email_intents"));
    }

    private static Task<HttpResponseMessage> ChangeAsync(HttpClient client, int revision, string password, string? value, string? description = null) =>
        client.PutAsJsonAsync(Root + "/" + Key, new { Revision = revision, CurrentPassword = password, Value = value, Description = description }, Cancellation);

    private static async Task<HttpClient> AdminAsync(BackofficeIntegrationApiFactory api, string email = "admin@adnerscarpelini.com")
    {
        var client = api.CreateClient();
        using var login = await client.PostAsJsonAsync("/api/v1/admin/identity/sessions", new { Email = email, Password }, Cancellation);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var tokens = await login.Content.ReadFromJsonAsync<JsonElement>(Cancellation);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return client;
    }

    private sealed class DevelopmentEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ParameterChangeTests";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
