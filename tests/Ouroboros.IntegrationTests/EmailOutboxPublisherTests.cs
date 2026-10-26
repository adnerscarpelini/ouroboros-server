using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ouroboros.Identity.Application.Security;
using Ouroboros.Identity.Domain.Notifications;
using Ouroboros.Identity.Domain.Users;
using Ouroboros.Identity.Infrastructure.Messaging;
using Ouroboros.Identity.Infrastructure.Persistence;
using Ouroboros.Messaging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Ouroboros.IntegrationTests;

public sealed class EmailOutboxPublisherTests(PostgresFixture postgres, RabbitMqFixture broker)
    : IntegrationTest(postgres), IClassFixture<RabbitMqFixture>
{
    private readonly PostgresFixture _postgres = postgres;
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Missing_fixed_route_suspends_until_explicit_resume_even_after_binding_is_restored()
    {
        await PurgeAsync();
        await RequestAsync("route-suspended");
        var original = await IntentAsync();
        await using var connection = await broker.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Cancellation);
        await channel.QueueUnbindAsync(RabbitMqFixture.Queue, RabbitMqFixture.Exchange, "email.requested.v1", cancellationToken: Cancellation);
        using var metrics = new EmailOutboxMetrics();
        using var publisher = Publisher(metrics);
        try
        {
            await publisher.RunOnceAsync(Cancellation);
            var suspended = await IntentAsync();
            Assert.Equal(EmailIntentState.Pending, suspended.State);
            Assert.Equal("configuration-suspended", suspended.LastFailureCode);
            Assert.Null(suspended.NextAttemptAt);
            Assert.NotNull(suspended.ProtectedEnvelope);
        }
        finally
        {
            await channel.QueueBindAsync(RabbitMqFixture.Queue, RabbitMqFixture.Exchange, "email.requested.v1", cancellationToken: Cancellation);
        }
        await publisher.RunOnceAsync(Cancellation);
        Assert.Equal(1, (await IntentAsync()).Attempts);
        Assert.Equal(EmailIntentState.Pending, (await IntentAsync()).State);
        var database = new Npgsql.NpgsqlConnectionStringBuilder(ConnectionString).Database!;
        await _postgres.ResumeEmailPublicationAsync(database, original.ExternalId);
        await publisher.RunOnceAsync(Cancellation);
        var completed = await IntentAsync();
        Assert.Equal(EmailIntentState.Published, completed.State);
        Assert.Equal(original.ExternalId, completed.ExternalId);
        Assert.Equal(original.ExpiresAt, completed.ExpiresAt);
    }

    [Fact]
    public async Task Previous_lease_owner_cannot_complete_retry_or_resurrect_a_reclaimed_then_cancelled_intent()
    {
        await PurgeAsync();
        await RequestAsync("fenced");
        await using var scope = Api.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<EmailOutboxStore>();
        var firstLease = Guid.CreateVersion7();
        var first = Assert.Single(await store.ClaimAsync(firstLease, DateTimeOffset.UtcNow, 1, Cancellation));
        await ExecuteAsync("UPDATE identity.email_intents SET lease_until=now()-interval '1 second'");
        var secondLease = Guid.CreateVersion7();
        var second = Assert.Single(await store.ClaimAsync(secondLease, DateTimeOffset.UtcNow, 1, Cancellation));
        Assert.Equal(first.ExternalId, second.ExternalId);
        Assert.Equal(0, await store.ConfirmAsync(first.Id, firstLease, DateTimeOffset.UtcNow, Cancellation));
        Assert.Equal(0, await store.FailAsync(first.Id, firstLease, DateTimeOffset.UtcNow, Cancellation));
        Assert.Equal(0, await store.RetryAsync(first.Id, firstLease, DateTimeOffset.UtcNow, null, "publish-unconfirmed", Cancellation));
        var pending = await IntentAsync();
        Assert.Equal(secondLease, pending.LeaseId);
        Assert.Equal(EmailIntentState.Pending, pending.State);
        Assert.NotNull(pending.ProtectedEnvelope);

        await RequestAsync("fenced");
        Assert.False(await store.IsOwnedAsync(first.Id, secondLease, DateTimeOffset.UtcNow, Cancellation));
        Assert.Equal(0, await store.ConfirmAsync(first.Id, secondLease, DateTimeOffset.UtcNow, Cancellation));
        Assert.Equal(0, await store.RetryAsync(first.Id, secondLease, DateTimeOffset.UtcNow, null, "publish-unconfirmed", Cancellation));
        using var metrics = new EmailOutboxMetrics();
        using var publisher = Publisher(metrics);
        await publisher.RunOnceAsync(Cancellation);
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var cancelled = await db.EmailIntents.AsNoTracking().SingleAsync(intent => intent.Id == first.Id, Cancellation);
        Assert.Equal(EmailIntentState.Cancelled, cancelled.State);
        Assert.Null(cancelled.ProtectedEnvelope);
        Assert.Null(cancelled.LeaseId);
        var published = await db.EmailIntents.AsNoTracking().SingleAsync(intent => intent.State == EmailIntentState.Published, Cancellation);
        await using var connection = await broker.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Cancellation);
        var delivery = await channel.BasicGetAsync(RabbitMqFixture.Queue, true, Cancellation);
        Assert.NotNull(delivery);
        Assert.Equal(published.ExternalId.ToString("D"), delivery.BasicProperties.MessageId);
        Assert.Null(await channel.BasicGetAsync(RabbitMqFixture.Queue, true, Cancellation));
    }

    [Fact]
    public async Task Invalid_stored_contract_fails_permanently_and_discards_payload_without_broker_publication()
    {
        await PurgeAsync();
        await RequestAsync("invalid");
        await ExecuteAsync("UPDATE identity.email_intents SET protected_envelope=convert_to('{}','UTF8')");
        using var metrics = new EmailOutboxMetrics();
        using var publisher = Publisher(metrics);
        await publisher.RunOnceAsync(Cancellation);
        var row = await IntentAsync();
        Assert.Equal(EmailIntentState.Failed, row.State);
        Assert.Equal("invalid-contract", row.LastFailureCode);
        Assert.Null(row.ProtectedEnvelope);
        Assert.Null(row.PublishedAt);
        await publisher.RunOnceAsync(Cancellation);
        Assert.Equal(1, (await IntentAsync()).Attempts);
        await using var connection = await broker.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Cancellation);
        Assert.Null(await channel.BasicGetAsync(RabbitMqFixture.Queue, true, Cancellation));
    }

    [Fact]
    public async Task Authentication_failure_suspends_until_explicit_sql_resume_preserving_identity_and_deadline()
    {
        await PurgeAsync();
        await RequestAsync("suspend");
        var wrong = Settings();
        wrong.Password = new string('0', 64);
        using var metrics = new EmailOutboxMetrics();
        using var denied = new EmailOutboxPublisher(Api.Services.GetRequiredService<IServiceScopeFactory>(), Options.Create(wrong),
            new DevelopmentEnvironment(), TimeProvider.System, metrics, NullLogger<EmailOutboxPublisher>.Instance);
        await denied.RunOnceAsync(Cancellation);
        var suspended = await IntentAsync();
        Assert.Equal(EmailIntentState.Pending, suspended.State);
        Assert.Equal("configuration-suspended", suspended.LastFailureCode);
        Assert.NotNull(suspended.ProtectedEnvelope);
        Assert.Null(suspended.LeaseId);
        using var corrected = Publisher(metrics);
        await corrected.RunOnceAsync(Cancellation);
        Assert.Equal(suspended.Attempts, (await IntentAsync()).Attempts);
        var database = new Npgsql.NpgsqlConnectionStringBuilder(ConnectionString).Database!;
        await _postgres.ResumeEmailPublicationAsync(database, suspended.ExternalId);
        await corrected.RunOnceAsync(Cancellation);
        var completed = await IntentAsync();
        Assert.Equal(EmailIntentState.Published, completed.State);
        Assert.Equal(suspended.ExternalId, completed.ExternalId);
        Assert.Equal(suspended.ExpiresAt, completed.ExpiresAt);
        Assert.Null(completed.ProtectedEnvelope);
    }

    [Fact]
    public async Task Enabled_background_publisher_does_not_prevent_host_start_with_broker_stopped()
    {
        await RequestAsync("startup");
        var settings = Settings();
        await broker.StopAsync();
        try
        {
            await using var enabled = Api.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Identity:EmailPublisher:Enabled"] = "true",
                    ["Identity:EmailPublisher:Host"] = settings.Host,
                    ["Identity:EmailPublisher:Port"] = settings.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["Identity:EmailPublisher:Password"] = settings.Password,
                })));
            using var client = enabled.CreateClient();
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/identity/keys", Cancellation)).StatusCode);
        }
        finally { await broker.StartAsync(); }
    }

    [Fact]
    public async Task Concurrent_publishers_respect_batch_limit_and_publish_each_claimed_identity_once()
    {
        await PurgeAsync();
        for (var i = 0; i < 9; i++) { await RequestAsync("batch-" + i); }
        using var metrics = new EmailOutboxMetrics();
        using var first = Publisher(metrics);
        using var second = Publisher(metrics);
        await Task.WhenAll(first.RunOnceAsync(Cancellation), second.RunOnceAsync(Cancellation));
        await using var scope = Api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        Assert.Equal(8, await db.EmailIntents.CountAsync(intent => intent.State == EmailIntentState.Published, Cancellation));
        Assert.Equal(1, await db.EmailIntents.CountAsync(intent => intent.State == EmailIntentState.Pending, Cancellation));
        await first.RunOnceAsync(Cancellation);
        await using var connection = await broker.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Cancellation);
        var ids = new List<string>();
        for (var i = 0; i < 9; i++)
        {
            var message = await channel.BasicGetAsync(RabbitMqFixture.Queue, true, Cancellation);
            Assert.NotNull(message);
            ids.Add(message.BasicProperties.MessageId!);
        }
        Assert.Equal(9, ids.Distinct().Count());
        Assert.Null(await channel.BasicGetAsync(RabbitMqFixture.Queue, true, Cancellation));
    }

    [Fact]
    public async Task Publication_logs_continue_the_request_trace_without_using_it_as_message_identity()
    {
        const string TraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
        await PurgeAsync();
        foreach (var email in new[] { "first-trace@example.com", "second-trace@example.com" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/identity/registrations") { Content = JsonContent.Create(new { Email = email }) };
            request.Headers.Add("traceparent", $"00-{TraceId}-00f067aa0ba902b7-01");
            using var response = await Client.SendAsync(request, Cancellation);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }
        using var metrics = new EmailOutboxMetrics();
        var logs = new TraceCapturingLogger<EmailOutboxPublisher>();
        using var unavailable = new EmailOutboxPublisher(Api.Services.GetRequiredService<IServiceScopeFactory>(), Options.Create(Settings(port: 1)),
            new DevelopmentEnvironment(), TimeProvider.System, metrics, logs);
        await unavailable.RunOnceAsync(Cancellation);

        var failures = logs.Entries.Where(entry => entry.Message.Contains("não confirmada", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, failures.Length);
        Assert.All(failures, entry => Assert.Equal(TraceId, entry.TraceId));
        Assert.NotEqual(failures[0].SpanId, failures[1].SpanId);
        await ExecuteAsync("UPDATE identity.email_intents SET next_attempt_at=NULL");
        using var publisher = Publisher(metrics);
        await publisher.RunOnceAsync(Cancellation);
        await using var connection = await broker.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Cancellation);
        var identities = new HashSet<Guid>();
        for (var index = 0; index < 2; index++)
        {
            var delivery = await channel.BasicGetAsync(RabbitMqFixture.Queue, true, Cancellation);
            Assert.NotNull(delivery);
            Assert.True(EnvelopeCodec.TryDecode(delivery.Body, "notification.email.requested", 1, "identity", out var message, out _));
            Assert.True(System.Diagnostics.ActivityContext.TryParse(message!.Traceparent, message.Tracestate, out var context));
            Assert.Equal(TraceId, context.TraceId.ToHexString());
            Assert.True(identities.Add(message.MessageId));
        }
    }

    [Fact]
    public async Task Confirmed_publication_discards_secret_and_preserves_id_in_broker_properties_and_envelope()
    {
        await PurgeAsync();
        await RequestAsync("confirmed");
        using var metrics = new EmailOutboxMetrics();
        using var publisher = Publisher(metrics);
        await publisher.RunOnceAsync(Cancellation);
        var row = await IntentAsync();
        Assert.Equal(EmailIntentState.Published, row.State);
        Assert.NotNull(row.PublishedAt);
        Assert.Null(row.ProtectedEnvelope);
        Assert.Null(row.ProtectionKeyId);
        Assert.Null(row.SupersessionKey);
        Assert.Null(row.Traceparent);
        Assert.Null(row.LeaseId);
        await using var connection = await broker.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Cancellation);
        var delivery = await channel.BasicGetAsync(RabbitMqFixture.Queue, true, Cancellation);
        Assert.NotNull(delivery);
        Assert.True(delivery.BasicProperties.Persistent);
        Assert.Equal(row.ExternalId.ToString("D"), delivery.BasicProperties.MessageId);
        Assert.True(EnvelopeCodec.TryDecode(delivery.Body, row.Type, row.Version, "identity", out var message, out _));
        Assert.Equal(row.ExternalId, message!.MessageId);
    }

    [Fact]
    public async Task Broker_unavailable_keeps_pending_with_bounded_backoff_while_login_works_then_recovers()
    {
        await PurgeAsync();
        await RequestAsync("unavailable");
        await using (var scope = Api.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            var hash = scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash("a sufficiently long customer password");
            db.Users.Add(User.Create("login@example.com", hash, UserRole.Customer));
            await db.SaveChangesAsync(Cancellation);
        }
        using var metrics = new EmailOutboxMetrics();
        using var down = Publisher(metrics);
        await broker.StopAsync();
        try
        {
            var publication = down.RunOnceAsync(Cancellation);
            using var login = await Client.PostAsJsonAsync("/api/v1/identity/sessions", new
            { Email = "login@example.com", Password = "a sufficiently long customer password" }, Cancellation);
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            await publication.WaitAsync(TimeSpan.FromSeconds(12), Cancellation);
        }
        finally { await broker.StartAsync(); }
        var row = await IntentAsync();
        Assert.Equal(EmailIntentState.Pending, row.State);
        Assert.NotNull(row.ProtectedEnvelope);
        Assert.Null(row.LeaseId);
        Assert.NotNull(row.LastFailureCode);
        Assert.InRange(row.NextAttemptAt!.Value - row.UpdatedAt, TimeSpan.Zero, TimeSpan.FromSeconds(60));
        await ExecuteAsync("UPDATE identity.email_intents SET next_attempt_at=NULL");
        using var recovered = Publisher(metrics);
        await recovered.RunOnceAsync(Cancellation);
        Assert.Equal(EmailIntentState.Published, (await IntentAsync()).State);
    }

    [Fact]
    public async Task Unroutable_publication_remains_recoverable_until_explicit_resume_after_repair()
    {
        await PurgeAsync();
        await RequestAsync("unroutable");
        await using var connection = await broker.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Cancellation);
        await channel.QueueUnbindAsync(RabbitMqFixture.Queue, RabbitMqFixture.Exchange, "email.requested.v1", cancellationToken: Cancellation);
        using var metrics = new EmailOutboxMetrics();
        using var publisher = Publisher(metrics);
        try
        {
            await publisher.RunOnceAsync(Cancellation);
            var row = await IntentAsync();
            Assert.Equal(EmailIntentState.Pending, row.State);
            Assert.Equal("configuration-suspended", row.LastFailureCode);
            Assert.NotNull(row.ProtectedEnvelope);
            Assert.Null(row.PublishedAt);
        }
        finally { await channel.QueueBindAsync(RabbitMqFixture.Queue, RabbitMqFixture.Exchange, "email.requested.v1", cancellationToken: Cancellation); }
        await _postgres.ResumeEmailPublicationAsync(new Npgsql.NpgsqlConnectionStringBuilder(ConnectionString).Database!, (await IntentAsync()).ExternalId);
        await publisher.RunOnceAsync(Cancellation);
        Assert.Equal(EmailIntentState.Published, (await IntentAsync()).State);
    }

    [Fact]
    public async Task Broker_nack_keeps_the_rejected_intent_pending()
    {
        await PurgeAsync();
        using var management = broker.ManagementClient();
        using var policy = await management.PutAsJsonAsync("policies/ouroboros/test-reject", new
        {
            pattern = "^notifications.email.v1$",
            priority = 100,
            definition = new Dictionary<string, object>
            { ["max-length"] = 1, ["overflow"] = "reject-publish" },
        }, Cancellation);
        policy.EnsureSuccessStatusCode();
        await Task.Delay(200, Cancellation);
        try
        {
            await using var connection = await broker.ConnectAsync();
            await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(true, true), Cancellation);
            var reachedLimit = false;
            for (var i = 0; i < 16; i++)
            {
                try { await channel.BasicPublishAsync(RabbitMqFixture.Exchange, "email.requested.v1", true, new BasicProperties { Persistent = true }, new byte[] { 1 }, Cancellation); }
                catch (PublishException) { reachedLimit = true; break; }
            }
            Assert.True(reachedLimit, "O broker real deve confirmar a rejeição do limite antes de testar a intenção.");
            await RequestAsync("rejected");
            using var metrics = new EmailOutboxMetrics();
            using var publisher = Publisher(metrics);
            await publisher.RunOnceAsync(Cancellation);
            await using var scope = Api.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            Assert.Equal(0, await db.EmailIntents.CountAsync(intent => intent.State == EmailIntentState.Published, Cancellation));
            var pending = await db.EmailIntents.SingleAsync(intent => intent.State == EmailIntentState.Pending, Cancellation);
            Assert.Equal("broker-rejected", pending.LastFailureCode);
            Assert.NotNull(pending.ProtectedEnvelope);
        }
        finally { using var deleted = await management.DeleteAsync("policies/ouroboros/test-reject", Cancellation); deleted.EnsureSuccessStatusCode(); }
    }

    [Fact]
    public async Task Crash_after_broker_confirm_before_progress_commit_redelivers_the_same_message_identity()
    {
        await PurgeAsync();
        await RequestAsync("crash");
        var lease = Guid.CreateVersion7();
        await using (var scope = Api.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<EmailOutboxStore>();
            var claimed = Assert.Single(await store.ClaimAsync(lease, DateTimeOffset.UtcNow, 1, Cancellation));
            await using var transport = new ConfirmedEmailPublisher(Options.Create(Settings()), new DevelopmentEnvironment());
            await transport.PublishAsync(claimed, Cancellation);
            // O processo termina sem ConfirmAsync. Lease/progresso continuam exclusivamente no PostgreSQL.
        }
        await ExecuteAsync("UPDATE identity.email_intents SET lease_until=now()-interval '1 second'");
        using var metrics = new EmailOutboxMetrics();
        using var restarted = Publisher(metrics);
        await restarted.RunOnceAsync(Cancellation);
        await using var connection = await broker.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Cancellation);
        var first = await channel.BasicGetAsync(RabbitMqFixture.Queue, true, Cancellation);
        var duplicate = await channel.BasicGetAsync(RabbitMqFixture.Queue, true, Cancellation);
        Assert.NotNull(first);
        Assert.NotNull(duplicate);
        Assert.Equal(first.BasicProperties.MessageId, duplicate.BasicProperties.MessageId);
        Assert.Equal(first.Body.ToArray(), duplicate.Body.ToArray());
        Assert.Equal(2, (await IntentAsync()).Attempts);
    }

    [Fact]
    public async Task Concurrent_claims_skip_locked_rows_and_leave_later_commits_eligible_without_high_water_cursor()
    {
        for (var i = 0; i < 6; i++) { await RequestAsync("concurrent-" + i); }
        await using var held = Api.Services.CreateAsyncScope();
        var locked = held.ServiceProvider.GetRequiredService<IdentityDbContext>();
        await using var transaction = await locked.Database.BeginTransactionAsync(Cancellation);
        await locked.Database.ExecuteSqlRawAsync("SELECT id FROM identity.email_intents ORDER BY id LIMIT 1 FOR UPDATE", Cancellation);
        await using var firstScope = Api.Services.CreateAsyncScope();
        await using var secondScope = Api.Services.CreateAsyncScope();
        var first = firstScope.ServiceProvider.GetRequiredService<EmailOutboxStore>().ClaimAsync(Guid.NewGuid(), DateTimeOffset.UtcNow, 4, Cancellation);
        var second = secondScope.ServiceProvider.GetRequiredService<EmailOutboxStore>().ClaimAsync(Guid.NewGuid(), DateTimeOffset.UtcNow, 4, Cancellation);
        var claimed = (await Task.WhenAll(first, second)).SelectMany(rows => rows).ToArray();
        Assert.Equal(5, claimed.Length);
        Assert.Equal(5, claimed.Select(row => row.Id).Distinct().Count());
        await transaction.CommitAsync(Cancellation);
        var late = Assert.Single(await firstScope.ServiceProvider.GetRequiredService<EmailOutboxStore>().ClaimAsync(Guid.NewGuid(), DateTimeOffset.UtcNow, 4, Cancellation));
        Assert.True(late.Id < claimed.Min(row => row.Id));
    }

    [Fact]
    public async Task Expired_intent_is_cleared_without_publication_and_confirmed_metadata_respects_retention()
    {
        await PurgeAsync();
        await RequestAsync("expired");
        await ExecuteAsync("UPDATE identity.email_intents SET occurred_at=now()-interval '2 days', expires_at=now()-interval '1 second'");
        using var metrics = new EmailOutboxMetrics();
        using var publisher = Publisher(metrics);
        await publisher.RunOnceAsync(Cancellation);
        var row = await IntentAsync();
        Assert.Equal(EmailIntentState.Expired, row.State);
        Assert.Null(row.ProtectedEnvelope);
        Assert.Equal(0, row.Attempts);
        await using var connection = await broker.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Cancellation);
        Assert.Null(await channel.BasicGetAsync(RabbitMqFixture.Queue, true, Cancellation));
        await RequestAsync("retention");
        await publisher.RunOnceAsync(Cancellation);
        await RequestAsync("still-pending");
        await using var scope = Api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        await ExecuteAsync("UPDATE identity.email_intents SET expires_at=now()-interval '31 days' WHERE state IN ('Published','Expired')");
        await EmailIntentPersistence.MaintainAsync(db, DateTimeOffset.UtcNow, Cancellation);
        Assert.Equal(EmailIntentState.Pending, (await db.EmailIntents.SingleAsync(Cancellation)).State);
    }

    [Fact]
    public async Task Metrics_expose_pending_age_failures_and_retries_without_message_tags()
    {
        await RequestAsync("metrics");
        var values = new Dictionary<string, double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        { if (instrument.Meter.Name == "Ouroboros.Identity.Outbox") { meterListener.EnableMeasurementEvents(instrument); } };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => { Assert.Empty(tags.ToArray()); values[instrument.Name] = value; });
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => { Assert.Empty(tags.ToArray()); values[instrument.Name] = value; });
        listener.Start();
        using var metrics = new EmailOutboxMetrics();
        using var publisher = Publisher(metrics, port: 1);
        await publisher.RunOnceAsync(Cancellation);
        await ExecuteAsync("UPDATE identity.email_intents SET next_attempt_at=NULL");
        await publisher.RunOnceAsync(Cancellation);
        listener.RecordObservableInstruments();
        Assert.Equal(1, values["outbox.backlog"]);
        Assert.True(values["outbox.oldest.age"] >= 0);
        Assert.Equal(1, values["outbox.publish.failures"]);
        Assert.Equal(1, values["outbox.publish.retries"]);
    }

    private EmailPublisherOptions Settings(int? port = null) => new()
    { Host = broker.Host, Port = port ?? broker.Port, Password = broker.IdentityPassword, BatchSize = 4 };

    private EmailOutboxPublisher Publisher(EmailOutboxMetrics metrics, int? port = null) => new(Api.Services.GetRequiredService<IServiceScopeFactory>(),
        Options.Create(Settings(port)), new DevelopmentEnvironment(), TimeProvider.System, metrics, NullLogger<EmailOutboxPublisher>.Instance);

    private async Task RequestAsync(string prefix)
    {
        using var response = await Client.PostAsJsonAsync("/api/v1/identity/registrations", new { Email = prefix + "@example.com" }, Cancellation);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    private async Task<EmailIntent> IntentAsync()
    {
        await using var scope = Api.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().EmailIntents.AsNoTracking().SingleAsync(Cancellation);
    }

    private async Task PurgeAsync()
    {
        await using var connection = await broker.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Cancellation);
        await channel.QueuePurgeAsync(RabbitMqFixture.Queue, Cancellation);
    }

    private sealed class DevelopmentEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "OutboxTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
