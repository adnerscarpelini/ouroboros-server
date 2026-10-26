using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ouroboros.Identity.Application.Notifications;
using Ouroboros.Identity.Domain.Notifications;
using Ouroboros.Identity.Infrastructure.Persistence;
using Ouroboros.Messaging;

namespace Ouroboros.IntegrationTests;

public sealed class EmailIntentTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private readonly PostgresFixture _postgres = postgres;
    private const string Email = "outbox@example.com";

    [Fact]
    public async Task Explicit_request_trace_context_is_preserved_in_the_committed_intent()
    {
        const string TraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/identity/registrations")
        {
            Content = JsonContent.Create(new { Email }),
        };
        request.Headers.Add("traceparent", $"00-{TraceId}-00f067aa0ba902b7-01");
        request.Headers.Add("tracestate", "vendor=opaque");
        using var response = await Client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await using var scope = Api.Services.CreateAsyncScope();
        var intent = await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().EmailIntents.AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.True(ActivityContext.TryParse(intent.Traceparent, intent.Tracestate, out var context));
        Assert.Equal(TraceId, context.TraceId.ToHexString());
        Assert.Equal("vendor=opaque", intent.Tracestate);
        Assert.True(EnvelopeCodec.TryDecode(intent.ProtectedEnvelope!, "notification.email.requested", 1, "identity", out var envelope, out _));
        Assert.Equal(intent.Traceparent, envelope!.Traceparent);
        Assert.Equal(intent.Tracestate, envelope.Tracestate);
        Assert.Equal(Guid.ParseExact(TraceId, "N"), envelope.CorrelationId);
    }

    [Fact]
    public async Task Intent_and_token_commit_together_with_stable_id_protected_recipient_and_origin_context()
    {
        using var activity = new Activity("origin").SetIdFormat(ActivityIdFormat.W3C).Start();
        activity.TraceStateString = "vendor=opaque";
        await RequestAsync();
        await using var scope = Api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var intent = await db.EmailIntents.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        var token = await db.EmailTokens.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(token.ExternalId, intent.SourceTokenId);
        Assert.Equal(token.ExpiresAt, intent.ExpiresAt);
        Assert.Equal(EmailIntentState.Pending, intent.State);
        Assert.Equal(0, intent.Attempts);
        Assert.Null(intent.PublishedAt);
        Assert.NotNull(intent.Traceparent);
        Assert.True(EnvelopeCodec.TryDecode(intent.ProtectedEnvelope!, "notification.email.requested", 1, "identity", out var envelope, out _));
        Assert.Equal(intent.ExternalId, envelope!.MessageId);
        Assert.Equal(token.ExpiresAt, envelope.ExpiresAt);
        Assert.Equal(intent.Traceparent, envelope.Traceparent);
        Assert.Equal(intent.Tracestate, envelope.Tracestate);
        var content = Assert.Single(await TestOutbox.ReadAsync(Api.Services));
        Assert.Equal(Email, content.To);
        Assert.Contains("solicitação mais recente", content.Body);
        var rawToken = await TestOutbox.TokenAsync(Api.Services, Email);
        var stored = Encoding.UTF8.GetString(intent.ProtectedEnvelope!);
        Assert.DoesNotContain(Email, stored);
        Assert.DoesNotContain(rawToken, stored);
        Assert.DoesNotContain("http://", stored);
        Assert.DoesNotContain(Email, intent.SupersessionKey!);
    }

    [Fact]
    public async Task Rollback_after_outbox_insert_restores_old_token_and_pending_intent_without_publishing_new_one()
    {
        await RequestAsync();
        var firstToken = await TestOutbox.TokenAsync(Api.Services, Email);
        await ExecuteAsync("""
            CREATE FUNCTION fail_intent_insert() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected intent failure'; END $$;
            CREATE TRIGGER fail_intent_insert AFTER INSERT ON identity.email_intents
            FOR EACH ROW EXECUTE FUNCTION fail_intent_insert();
            """);
        await RequestAsync();
        Assert.Equal(1L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.email_tokens"));
        Assert.Equal(1L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.email_intents"));
        Assert.Null(await QueryScalarAsync<DateTime?>("SELECT revoked_at FROM identity.email_tokens"));
        Assert.Equal("Pending", await QueryScalarAsync<string>("SELECT state FROM identity.email_intents"));
        Assert.Equal(firstToken, await TestOutbox.TokenAsync(Api.Services, Email));
    }

    [Fact]
    public async Task Concurrent_replacement_leaves_one_intent_and_clears_replaced_payloads_atomically()
    {
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => RequestAsync()));
        Assert.Equal(6L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.email_tokens"));
        Assert.Equal(1L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.email_tokens WHERE revoked_at IS NULL"));
        Assert.Equal(1L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.email_intents WHERE state='Pending' AND protected_envelope IS NOT NULL"));
        Assert.Equal(5L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.email_intents WHERE state='Cancelled' AND protected_envelope IS NULL AND supersession_key IS NULL AND protection_key_id IS NULL"));
    }

    [Fact]
    public async Task Expiry_cleanup_clears_secret_and_retains_only_terminal_metadata_until_thirty_days_after_deadline()
    {
        await RequestAsync();
        DateTimeOffset deadline;
        await using (var scope = Api.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            deadline = (await db.EmailIntents.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken)).ExpiresAt;
            await EmailIntentPersistence.MaintainAsync(db, deadline, TestContext.Current.CancellationToken);
        }
        Assert.Equal("Expired", await QueryScalarAsync<string>("SELECT state FROM identity.email_intents"));
        Assert.Equal(0L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.email_intents WHERE protected_envelope IS NOT NULL OR supersession_key IS NOT NULL OR protection_key_id IS NOT NULL OR traceparent IS NOT NULL OR tracestate IS NOT NULL"));
        await using var cleanup = Api.Services.CreateAsyncScope();
        var context = cleanup.ServiceProvider.GetRequiredService<IdentityDbContext>();
        await EmailIntentPersistence.MaintainAsync(context, deadline.AddDays(30), TestContext.Current.CancellationToken);
        Assert.Equal(1, await context.EmailIntents.CountAsync(TestContext.Current.CancellationToken));
        await EmailIntentPersistence.MaintainAsync(context, deadline.AddDays(30).AddTicks(10), TestContext.Current.CancellationToken);
        Assert.Equal(0, await context.EmailIntents.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Committed_intent_survives_new_process_scope_and_real_postgres_backup_restore()
    {
        await RequestAsync();
        var token = await TestOutbox.TokenAsync(Api.Services, Email);
        var source = new NpgsqlConnectionStringBuilder(ConnectionString).Database!;
        var restored = await _postgres.CreateDatabaseAsync(useTemplate: false);
        try
        {
            await _postgres.BackupAndRestoreAsync(source, restored);
            await using var copy = new IntegrationApiFactory(_postgres.ConnectionStringFor(restored));
            Assert.Equal(token, await TestOutbox.TokenAsync(copy.Services, Email));
            await using var scope = copy.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            var intent = await db.EmailIntents.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain(token, Encoding.UTF8.GetString(intent.ProtectedEnvelope!));
            // Restauração sem publicar: limpar vencidos antes de habilitar o processamento.
            await EmailIntentPersistence.MaintainAsync(db, intent.ExpiresAt, TestContext.Current.CancellationToken);
            Assert.Null((await db.EmailIntents.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken)).ProtectedEnvelope);
        }
        finally { NpgsqlConnection.ClearAllPools(); await _postgres.DropDatabaseAsync(restored); }
    }

    private async Task RequestAsync()
    {
        using var response = await Client.PostAsJsonAsync("/api/v1/identity/registrations", new { Email }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }
}
