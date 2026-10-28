extern alias identityapi;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Ouroboros.Identity.Application.Notifications;
using Ouroboros.Identity.Application.Security;
using Ouroboros.Identity.Domain.Users;
using Ouroboros.Identity.Infrastructure.Persistence;

namespace Ouroboros.IntegrationTests;

public sealed class AdminInvitationTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private const string Root = "/api/v1/admin/identity/admin-invitations";
    internal const string Password = "uma frase muito longa para os administradores";
    internal const string Recipient = "convidado@adnerscarpelini.com";
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Single_admin_invites_and_recipient_completes_once_with_audited_admin_account()
    {
        var author = await PrepareAsync();
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        using var admin = await AdminAsync(api);
        using var response = await InviteAsync(admin);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.DoesNotContain("token", await response.Content.ReadAsStringAsync(Cancellation), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0L, await QueryScalarAsync<long>($"SELECT count(*) FROM identity.users WHERE email = '{Recipient}'"));
        var messages = await TestOutbox.ReadAsync(api.Services);
        Assert.Equal(2, messages.Count);
        var token = await TestOutbox.TokenAsync(api.Services, Recipient);
        Assert.NotEmpty(token);
        Assert.DoesNotContain(messages.Where(message => message.To != Recipient), message => message.Body.Contains(token, StringComparison.Ordinal));
        Assert.NotEqual(token, await QueryScalarAsync<string>("SELECT encode(token_hash, 'hex') FROM identity.admin_invitations"));
        using var anonymous = api.CreateClient();
        using var accepted = await anonymous.PostAsJsonAsync(Root + "/complete", new { Token = token, Password }, Cancellation);
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        Assert.Equal("Admin", await QueryScalarAsync<string>($"SELECT role FROM identity.users WHERE email = '{Recipient}'"));
        Assert.Equal(author, await QueryScalarAsync<Guid>($"SELECT invited_by FROM identity.users WHERE email = '{Recipient}'"));
        Assert.Equal("Completed", await QueryScalarAsync<string>("SELECT state FROM identity.admin_invitations"));
        using var reused = await anonymous.PostAsJsonAsync(Root + "/complete", new { Token = token, Password }, Cancellation);
        Assert.Equal(HttpStatusCode.BadRequest, reused.StatusCode);
        using var login = await anonymous.PostAsJsonAsync("/api/v1/admin/identity/sessions", new { Email = Recipient, Password }, Cancellation);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(4, (await TestOutbox.ReadAsync(api.Services)).Count);
    }

    [Theory]
    [InlineData("bad-password", "novo@adnerscarpelini.com", HttpStatusCode.Unauthorized)]
    [InlineData(Password, "novo@sub.adnerscarpelini.com", HttpStatusCode.BadRequest)]
    [InlineData(Password, "novo@example.com", HttpStatusCode.BadRequest)]
    [InlineData(Password, "admin@adnerscarpelini.com", HttpStatusCode.Conflict)]
    public async Task Refused_creation_has_no_invitation_or_email(string password, string email, HttpStatusCode expected)
    {
        await PrepareAsync();
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        using var client = await AdminAsync(api);
        using var response = await client.PostAsJsonAsync(Root, new { Email = email, CurrentPassword = password }, Cancellation);
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(0L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.admin_invitations"));
        Assert.Empty(await TestOutbox.ReadAsync(api.Services));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Customer_or_demoted_admin_is_forbidden(bool demoted)
    {
        await PrepareAsync();
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        using var client = await AdminAsync(api);
        await ExecuteAsync("UPDATE identity.users SET role = 'Customer' WHERE email = 'admin@adnerscarpelini.com'");
        if (!demoted)
        {
            using var external = Api.CreateClient();
            using var login = await external.PostAsJsonAsync("/api/v1/identity/sessions", new { Email = "admin@adnerscarpelini.com", Password }, Cancellation);
            var body = await login.Content.ReadFromJsonAsync<JsonElement>(Cancellation);
            client.DefaultRequestHeaders.Authorization = new("Bearer", body.GetProperty("accessToken").GetString());
        }
        using var response = await InviteAsync(client);
        Assert.Equal(demoted ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await TestOutbox.ReadAsync(api.Services));
    }

    [Fact]
    public async Task Two_admins_create_pending_invitation_without_link_to_recipient()
    {
        await PrepareAsync(twoAdmins: true);
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        using var client = await AdminAsync(api);
        using var response = await InviteAsync(client);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("PendingApproval", await QueryScalarAsync<string>("SELECT state FROM identity.admin_invitations"));
        Assert.Equal(0L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.admin_invitations WHERE token_hash IS NOT NULL"));
        Assert.DoesNotContain(await TestOutbox.ReadAsync(api.Services), message => message.To == Recipient);
    }

    [Fact]
    public async Task Concurrent_completions_create_only_one_account_and_one_set_of_notices()
    {
        await PrepareAsync();
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        using var client = await AdminAsync(api);
        using var invitation = await InviteAsync(client);
        var token = await TestOutbox.TokenAsync(api.Services, Recipient);
        using var first = api.CreateClient();
        using var second = api.CreateClient();
        var replies = await Task.WhenAll(first.PostAsJsonAsync(Root + "/complete", new { Token = token, Password }, Cancellation),
            second.PostAsJsonAsync(Root + "/complete", new { Token = token, Password }, Cancellation));
        try
        {
            Assert.Equal(1, replies.Count(reply => reply.StatusCode == HttpStatusCode.NoContent));
            Assert.Equal(1, replies.Count(reply => reply.StatusCode == HttpStatusCode.BadRequest));
            Assert.Equal(1L, await QueryScalarAsync<long>($"SELECT count(*) FROM identity.users WHERE email = '{Recipient}'"));
            Assert.Equal(4, (await TestOutbox.ReadAsync(api.Services)).Count);
        }
        finally { foreach (var response in replies) { response.Dispose(); } }
    }

    [Fact]
    public async Task Password_rejection_does_not_consume_link_and_expired_link_does_not_create_account()
    {
        await PrepareAsync();
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        using var client = await AdminAsync(api);
        using var invitation = await InviteAsync(client);
        var token = await TestOutbox.TokenAsync(api.Services, Recipient);
        using var anonymous = api.CreateClient();
        using var weak = await anonymous.PostAsJsonAsync(Root + "/complete", new { Token = token, Password = "123" }, Cancellation);
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        Assert.Equal("Approved", await QueryScalarAsync<string>("SELECT state FROM identity.admin_invitations"));
        await ExecuteAsync("UPDATE identity.admin_invitations SET token_expires_at = now() - interval '1 second'");
        using var expired = await anonymous.PostAsJsonAsync(Root + "/complete", new { Token = token, Password }, Cancellation);
        Assert.Equal(HttpStatusCode.BadRequest, expired.StatusCode);
        Assert.Equal(0L, await QueryScalarAsync<long>($"SELECT count(*) FROM identity.users WHERE email = '{Recipient}'"));
    }

    [Fact]
    public async Task Email_protection_failure_rolls_back_invitation()
    {
        await PrepareAsync();
        await using var baseApi = new BackofficeIntegrationApiFactory(ConnectionString);
        await using var api = baseApi.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        { services.RemoveAll<IEmailIntentFactory>(); services.AddSingleton<IEmailIntentFactory, FailingEmailIntentFactory>(); }));
        using var client = await AdminAsync(api);
        using var response = await InviteAsync(client);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(0L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.admin_invitations"));
        Assert.Equal(0L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.email_intents"));
    }

    [Fact]
    public async Task Existing_customer_is_not_promoted_by_invitation()
    {
        await PrepareAsync();
        await using (var scope = Api.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            context.Users.Add(User.Create(Recipient, scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(Password), UserRole.Customer));
            await context.SaveChangesAsync(Cancellation);
        }
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        using var client = await AdminAsync(api);
        using var response = await InviteAsync(client);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Customer", await QueryScalarAsync<string>($"SELECT role FROM identity.users WHERE email = '{Recipient}'"));
        Assert.Empty(await TestOutbox.ReadAsync(api.Services));
    }

    [Fact]
    public async Task Concurrent_duplicate_invitations_create_one_link_and_one_notice()
    {
        await PrepareAsync();
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        using var first = await AdminAsync(api);
        using var second = await AdminAsync(api);
        var replies = await Task.WhenAll(InviteAsync(first), InviteAsync(second));
        try
        {
            Assert.Equal(1, replies.Count(reply => reply.StatusCode == HttpStatusCode.Accepted));
            Assert.Equal(1, replies.Count(reply => reply.StatusCode == HttpStatusCode.Conflict));
            Assert.Equal(1L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.admin_invitations"));
            Assert.Equal(2, (await TestOutbox.ReadAsync(api.Services)).Count);
        }
        finally { foreach (var response in replies) { response.Dispose(); } }
    }

    [Fact]
    public async Task Notice_storage_failure_rolls_back_account_and_token_consumption()
    {
        await PrepareAsync();
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        using var client = await AdminAsync(api);
        using var response = await InviteAsync(client);
        var token = await TestOutbox.TokenAsync(api.Services, Recipient);
        await ExecuteAsync("""
            CREATE FUNCTION identity.fail_invite_notice() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'induced-notice-storage-failure'; END $$;
            CREATE TRIGGER fail_invite_notice BEFORE INSERT ON identity.email_intents
            FOR EACH ROW EXECUTE FUNCTION identity.fail_invite_notice();
            """);
        using var anonymous = api.CreateClient();
        using var completion = await anonymous.PostAsJsonAsync(Root + "/complete", new { Token = token, Password }, Cancellation);
        Assert.Equal(HttpStatusCode.InternalServerError, completion.StatusCode);
        Assert.Equal("Approved", await QueryScalarAsync<string>("SELECT state FROM identity.admin_invitations"));
        Assert.Equal(0L, await QueryScalarAsync<long>($"SELECT count(*) FROM identity.users WHERE email = '{Recipient}'"));
        Assert.Equal(2L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.email_intents"));
    }

    [Fact]
    public async Task Customer_with_administrative_audience_is_forbidden_without_writes()
    {
        var author = await PrepareAsync();
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        using var client = api.CreateClient();
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "ouroboros",
            Audience = "ouroboros-backoffice",
            Expires = DateTime.UtcNow.AddMinutes(5),
            Subject = new ClaimsIdentity([new Claim("sub", author.ToString()), new Claim("role", "Customer"),
                new Claim("principal_type", "user"), new Claim("sid", Guid.CreateVersion7().ToString())]),
            SigningCredentials = new SigningCredentials(TestSigningKeys.Key, SecurityAlgorithms.RsaSha256),
        });
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        using var response = await InviteAsync(client);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.admin_invitations"));
        Assert.Empty(await TestOutbox.ReadAsync(api.Services));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invitation_reauthentication_rechecks_state_after_waiting_for_user_lock(bool changeRole)
    {
        await PrepareAsync();
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        using var client = await AdminAsync(api);
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(Cancellation);
        await using var transaction = await connection.BeginTransactionAsync(Cancellation);
        await using (var command = new NpgsqlCommand("SELECT 1 FROM identity.users WHERE email = 'admin@adnerscarpelini.com' FOR NO KEY UPDATE", connection, transaction))
        { await command.ExecuteScalarAsync(Cancellation); }
        var pending = InviteAsync(client);
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
        { await command.ExecuteNonQueryAsync(Cancellation); }
        await transaction.CommitAsync(Cancellation);
        using var response = await pending;
        Assert.Equal(changeRole ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.admin_invitations"));
        Assert.Empty(await TestOutbox.ReadAsync(api.Services));
    }

    [Fact]
    public async Task Anonymous_invalid_completion_is_generic_and_rate_limited_without_creating_account()
    {
        await using var baseApi = new BackofficeIntegrationApiFactory(ConnectionString);
        await using var api = baseApi.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(configuration =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Identity:RateLimiting:Policies:identity-registration-completion:PermitLimit"] = "10",
                ["Identity:RateLimiting:Policies:identity-registration-completion:Window"] = "01:00:00",
            })));
        using var client = api.CreateClient();
        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var invalid = await client.PostAsJsonAsync(Root + "/complete", new { Token = "unknown-invitation-token", Password }, Cancellation);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.DoesNotContain("unknown-invitation-token", await invalid.Content.ReadAsStringAsync(Cancellation));
        }
        using var limited = await client.PostAsJsonAsync(Root + "/complete", new { Token = "unknown-invitation-token", Password }, Cancellation);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(0L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.users"));
        Assert.Empty(await TestOutbox.ReadAsync(api.Services));
    }

    private async Task<Guid> PrepareAsync(bool twoAdmins = false)
    {
        await using var scope = Api.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var hash = scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(Password);
        var author = User.Create("admin@adnerscarpelini.com", hash, UserRole.Admin);
        context.Users.Add(author);
        if (twoAdmins) { context.Users.Add(User.Create("oi@adnerscarpelini.com", hash, UserRole.Admin)); }
        await context.SaveChangesAsync(Cancellation);
        return author.ExternalId;
    }

    internal static Task<HttpResponseMessage> InviteAsync(HttpClient client) =>
        client.PostAsJsonAsync(Root, new { Email = Recipient, CurrentPassword = Password }, Cancellation);

    internal static async Task<HttpClient> AdminAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<identityapi::Ouroboros.Identity.Api.IdentityApiEntryPoint> api, string email = "admin@adnerscarpelini.com")
    {
        var client = api.CreateClient();
        using var login = await client.PostAsJsonAsync("/api/v1/admin/identity/sessions", new { Email = email, Password }, Cancellation);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var tokens = await login.Content.ReadFromJsonAsync<JsonElement>(Cancellation);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return client;
    }
}
