extern alias identityapi;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ouroboros.Identity.Application.Security;
using Ouroboros.Identity.Domain.Notifications;
using Ouroboros.Identity.Domain.Users;
using Ouroboros.Identity.Infrastructure.Messaging;
using Ouroboros.Identity.Infrastructure.Persistence;

namespace Ouroboros.IntegrationTests;

public sealed class AdminInvitationApprovalTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private const string Root = "/api/v1/admin/identity/admin-invitations";
    private const string Password = AdminInvitationTests.Password;
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Another_designated_admin_approves_and_recipient_completes()
    {
        var approver = await PrepareAsync();
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        var id = await CreateAsync(api);
        var pending = await TestOutbox.ReadAsync(api.Services);
        Assert.DoesNotContain(pending, message => message.To == AdminInvitationTests.Recipient);
        Assert.Contains(pending, message => message.To == "oi@adnerscarpelini.com" && message.Body.Contains("invitationId=" + id));
        using var reviewer = await AdminInvitationTests.AdminAsync(api, "oi@adnerscarpelini.com");
        using var decision = await DecideAsync(reviewer, id, true);
        Assert.Equal(HttpStatusCode.NoContent, decision.StatusCode);
        Assert.Equal("Approved", await QueryScalarAsync<string>("SELECT state FROM identity.admin_invitations"));
        Assert.Equal(approver, await QueryScalarAsync<Guid>("SELECT decided_by FROM identity.admin_invitations"));
        Assert.Equal(1L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.admin_invitations WHERE decided_at IS NOT NULL"));
        var token = await TestOutbox.TokenAsync(api.Services, AdminInvitationTests.Recipient);
        Assert.NotEmpty(token);
        using var anonymous = api.CreateClient();
        using var completion = await anonymous.PostAsJsonAsync(Root + "/complete", new { Token = token, Password }, Cancellation);
        Assert.Equal(HttpStatusCode.NoContent, completion.StatusCode);
        Assert.DoesNotContain((await TestOutbox.ReadAsync(api.Services)).Where(message => message.To != AdminInvitationTests.Recipient),
            message => message.Body.Contains(token));
    }

    [Fact]
    public async Task Rejection_is_audited_without_link_to_recipient()
    {
        var approver = await PrepareAsync();
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        var id = await CreateAsync(api);
        using var reviewer = await AdminInvitationTests.AdminAsync(api, "oi@adnerscarpelini.com");
        using var decision = await DecideAsync(reviewer, id, false);
        Assert.Equal(HttpStatusCode.NoContent, decision.StatusCode);
        Assert.Equal("Rejected", await QueryScalarAsync<string>("SELECT state FROM identity.admin_invitations"));
        Assert.Equal(approver, await QueryScalarAsync<Guid>("SELECT decided_by FROM identity.admin_invitations"));
        Assert.DoesNotContain(await TestOutbox.ReadAsync(api.Services), message => message.To == AdminInvitationTests.Recipient);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Requester_cannot_decide_even_when_designated(bool approve)
    {
        await PrepareAsync();
        await SetApproversAsync("admin@adnerscarpelini.com,oi@adnerscarpelini.com");
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        var id = await CreateAsync(api);
        using var requester = await AdminInvitationTests.AdminAsync(api);
        using var response = await DecideAsync(requester, id, approve);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("PendingApproval", await QueryScalarAsync<string>("SELECT state FROM identity.admin_invitations"));
    }

    [Fact]
    public async Task Only_requester_as_approver_refuses_creation_without_writes()
    {
        await PrepareAsync();
        await SetApproversAsync("admin@adnerscarpelini.com");
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        using var requester = await AdminInvitationTests.AdminAsync(api);
        using var response = await AdminInvitationTests.InviteAsync(requester);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("aprovador", await response.Content.ReadAsStringAsync(Cancellation));
        Assert.Equal(0L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.admin_invitations"));
        Assert.Empty(await TestOutbox.ReadAsync(api.Services));
    }

    [Fact]
    public async Task Changed_approver_configuration_is_effective_at_decision()
    {
        await PrepareAsync();
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        var id = await CreateAsync(api);
        await SetApproversAsync("admin@adnerscarpelini.com");
        using var reviewer = await AdminInvitationTests.AdminAsync(api, "oi@adnerscarpelini.com");
        using var response = await DecideAsync(reviewer, id, true);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("PendingApproval", await QueryScalarAsync<string>("SELECT state FROM identity.admin_invitations"));
    }

    [Fact]
    public async Task Expired_pending_invitation_is_persisted_expired_without_link()
    {
        await PrepareAsync();
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        var id = await CreateAsync(api);
        await ExecuteAsync("UPDATE identity.admin_invitations SET approval_expires_at = now() - interval '1 second'");
        using var reviewer = await AdminInvitationTests.AdminAsync(api, "oi@adnerscarpelini.com");
        using var response = await DecideAsync(reviewer, id, true);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Expired", await QueryScalarAsync<string>("SELECT state FROM identity.admin_invitations"));
        Assert.DoesNotContain(await TestOutbox.ReadAsync(api.Services), message => message.To == AdminInvitationTests.Recipient);
    }

    [Fact]
    public async Task Wrong_password_does_not_decide_or_create_more_notices()
    {
        await PrepareAsync();
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        var id = await CreateAsync(api);
        var before = (await TestOutbox.ReadAsync(api.Services)).Count;
        using var reviewer = await AdminInvitationTests.AdminAsync(api, "oi@adnerscarpelini.com");
        using var response = await DecideAsync(reviewer, id, true, "incorrect-password");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("PendingApproval", await QueryScalarAsync<string>("SELECT state FROM identity.admin_invitations"));
        Assert.Equal(before, (await TestOutbox.ReadAsync(api.Services)).Count);
    }

    [Fact]
    public async Task Concurrent_approval_and_rejection_have_only_one_winner()
    {
        await PrepareAsync();
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        var id = await CreateAsync(api);
        using var first = await AdminInvitationTests.AdminAsync(api, "oi@adnerscarpelini.com");
        using var second = await AdminInvitationTests.AdminAsync(api, "oi@adnerscarpelini.com");
        var replies = await Task.WhenAll(DecideAsync(first, id, true), DecideAsync(second, id, false));
        try
        {
            Assert.Equal(1, replies.Count(reply => reply.StatusCode == HttpStatusCode.NoContent));
            Assert.Equal(1, replies.Count(reply => reply.StatusCode == HttpStatusCode.Conflict));
            var approved = await QueryScalarAsync<string>("SELECT state FROM identity.admin_invitations") == "Approved";
            var messages = await TestOutbox.ReadAsync(api.Services);
            Assert.Equal(approved ? 1 : 0, messages.Count(message => message.To == AdminInvitationTests.Recipient));
            Assert.Equal(approved ? 6 : 5, messages.Count);
        }
        finally { foreach (var reply in replies) { reply.Dispose(); } }
    }

    [Fact]
    public async Task Outside_domain_admin_is_not_an_approver_and_does_not_receive_review_link()
    {
        await PrepareAsync();
        await using (var scope = Api.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            context.Users.Add(User.Create("outsider@example.com", scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(Password), UserRole.Admin));
            await context.SaveChangesAsync(Cancellation);
        }
        await SetApproversAsync("outsider@example.com,oi@adnerscarpelini.com");
        var logs = new ConcurrentQueue<string>();
        await using var baseApi = new BackofficeIntegrationApiFactory(ConnectionString);
        await using var api = baseApi.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<ILoggerProvider>(new ListLoggerProvider(logs))));
        var id = await CreateAsync(api);
        Assert.DoesNotContain(await TestOutbox.ReadAsync(api.Services), message => message.To == "outsider@example.com" && message.Body.Contains("invitationId="));
        using var outsider = await AdminInvitationTests.AdminAsync(api, "outsider@example.com");
        using var response = await DecideAsync(outsider, id, true);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(logs, entry => entry.Contains("entradas inválidas ou fora do domínio permitido", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, entry => entry.Contains("outsider@example.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Failed_notice_insert_rolls_back_decision_and_link()
    {
        await PrepareAsync();
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        var id = await CreateAsync(api);
        await ExecuteAsync("""
            CREATE FUNCTION identity.fail_decision_notice() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'induced-decision-storage-failure'; END $$;
            CREATE TRIGGER fail_decision_notice BEFORE INSERT ON identity.email_intents
            FOR EACH ROW EXECUTE FUNCTION identity.fail_decision_notice();
            """);
        using var reviewer = await AdminInvitationTests.AdminAsync(api, "oi@adnerscarpelini.com");
        using var response = await DecideAsync(reviewer, id, true);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("PendingApproval", await QueryScalarAsync<string>("SELECT state FROM identity.admin_invitations"));
        Assert.Equal(0L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.admin_invitations WHERE token_hash IS NOT NULL OR decided_by IS NOT NULL"));
        Assert.Equal(3, (await TestOutbox.ReadAsync(api.Services)).Count);
    }

    [Theory]
    [InlineData("role = 'Customer'")]
    [InlineData("is_active = false")]
    public async Task Former_approver_cannot_decide_using_an_earlier_admin_token(string change)
    {
        await PrepareAsync();
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        var id = await CreateAsync(api);
        using var reviewer = await AdminInvitationTests.AdminAsync(api, "oi@adnerscarpelini.com");
        var before = (await TestOutbox.ReadAsync(api.Services)).Count;
        await ExecuteAsync("UPDATE identity.users SET " + change + " WHERE email = 'oi@adnerscarpelini.com'");
        using var response = await DecideAsync(reviewer, id, true);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("PendingApproval", await QueryScalarAsync<string>("SELECT state FROM identity.admin_invitations"));
        Assert.Equal(before, (await TestOutbox.ReadAsync(api.Services)).Count);
    }

    [Fact]
    public async Task Different_approvers_racing_on_a_decision_record_exactly_one_actor_and_notice_set()
    {
        var firstId = await PrepareAsync();
        Guid secondId;
        await using (var scope = Api.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            var second = User.Create("second@adnerscarpelini.com", scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(Password), UserRole.Admin);
            db.Users.Add(second);
            await db.SaveChangesAsync(Cancellation);
            secondId = second.ExternalId;
        }
        await SetApproversAsync("oi@adnerscarpelini.com,second@adnerscarpelini.com");
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        var id = await CreateAsync(api);
        using var first = await AdminInvitationTests.AdminAsync(api, "oi@adnerscarpelini.com");
        using var secondClient = await AdminInvitationTests.AdminAsync(api, "second@adnerscarpelini.com");
        var replies = await Task.WhenAll(DecideAsync(first, id, true), DecideAsync(secondClient, id, false));
        try
        {
            Assert.Equal(1, replies.Count(reply => reply.StatusCode == HttpStatusCode.NoContent));
            Assert.Equal(1, replies.Count(reply => reply.StatusCode == HttpStatusCode.Conflict));
            var approved = replies[0].StatusCode == HttpStatusCode.NoContent;
            Assert.Equal(approved ? "Approved" : "Rejected", await QueryScalarAsync<string>("SELECT state FROM identity.admin_invitations"));
            Assert.Equal(approved ? firstId : secondId, await QueryScalarAsync<Guid>("SELECT decided_by FROM identity.admin_invitations"));
            var messages = await TestOutbox.ReadAsync(api.Services);
            Assert.Equal(approved ? 9 : 8, messages.Count);
            Assert.Equal(approved ? 1 : 0, messages.Count(message => message.To == AdminInvitationTests.Recipient));
        }
        finally { foreach (var reply in replies) { reply.Dispose(); } }
    }

    [Fact]
    public async Task Publication_failure_after_approval_preserves_decision_and_recoverable_intents()
    {
        await PrepareAsync();
        await using var api = new BackofficeIntegrationApiFactory(ConnectionString);
        var id = await CreateAsync(api);
        using var reviewer = await AdminInvitationTests.AdminAsync(api, "oi@adnerscarpelini.com");
        using var decision = await DecideAsync(reviewer, id, true);
        Assert.Equal(HttpStatusCode.NoContent, decision.StatusCode);
        var hash = await QueryScalarAsync<string>("SELECT encode(token_hash, 'hex') FROM identity.admin_invitations");
        using var metrics = new EmailOutboxMetrics();
        var environment = new HostApplicationBuilder(new HostApplicationBuilderSettings
        { DisableDefaults = true, EnvironmentName = Environments.Development }).Environment;
        using var publisher = new EmailOutboxPublisher(api.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new EmailPublisherOptions { Host = "127.0.0.1", Port = 1, Password = new string('x', 40), BatchSize = 8 }),
            environment, TimeProvider.System, metrics, NullLogger<EmailOutboxPublisher>.Instance);
        await publisher.RunOnceAsync(Cancellation);
        Assert.Equal("Approved", await QueryScalarAsync<string>("SELECT state FROM identity.admin_invitations"));
        Assert.Equal(hash, await QueryScalarAsync<string>("SELECT encode(token_hash, 'hex') FROM identity.admin_invitations"));
        await using var scope = api.Services.CreateAsyncScope();
        var intents = await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().EmailIntents.AsNoTracking().ToArrayAsync(Cancellation);
        Assert.Equal(6, intents.Length);
        Assert.All(intents, intent =>
        {
            Assert.Equal(EmailIntentState.Pending, intent.State);
            Assert.NotNull(intent.ProtectedEnvelope);
            Assert.True(intent.Attempts > 0);
            Assert.NotNull(intent.NextAttemptAt);
        });
        using var duplicate = await DecideAsync(reviewer, id, false);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    private async Task<Guid> PrepareAsync()
    {
        await using var scope = Api.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var hash = scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(Password);
        var approver = User.Create("oi@adnerscarpelini.com", hash, UserRole.Admin);
        context.Users.AddRange(User.Create("admin@adnerscarpelini.com", hash, UserRole.Admin), approver);
        await context.SaveChangesAsync(Cancellation);
        return approver.ExternalId;
    }

    private Task SetApproversAsync(string emails) => ExecuteAsync($"UPDATE identity.parameters SET value = '{emails}' WHERE key = 'Identity:AdminInvitations:ApproverEmails'");
    private static async Task<Guid> CreateAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<identityapi::Ouroboros.Identity.Api.IdentityApiEntryPoint> api)
    {
        using var requester = await AdminInvitationTests.AdminAsync(api);
        using var response = await AdminInvitationTests.InviteAsync(requester);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Cancellation)).GetProperty("id").GetGuid();
    }
    private static Task<HttpResponseMessage> DecideAsync(HttpClient client, Guid id, bool approve, string password = Password) =>
        client.PostAsJsonAsync(Root + "/" + id + (approve ? "/approval" : "/rejection"), new { CurrentPassword = password }, Cancellation);
}
