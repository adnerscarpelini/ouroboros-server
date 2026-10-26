using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ouroboros.Messaging;
using Ouroboros.Messaging.Contracts;
using Ouroboros.Notifications.Domain;
using Ouroboros.Notifications.Infrastructure;
using Ouroboros.Notifications.Infrastructure.Persistence;
using RabbitMQ.Client;

namespace Ouroboros.IntegrationTests;

public sealed partial class NotificationsWorkerTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Rejected_contract_with_expiry_near_datetime_limit_can_publish_its_sanitized_failure_reference()
    {
        await PurgeAsync();
        await using var scenario = await NotificationDatabaseScenario.CreateAsync(database);
        var message = scenario.Message(expires: new DateTimeOffset(9999, 12, 15, 0, 0, 0, TimeSpan.Zero)) with { Version = 2 };
        Assert.Equal(AcceptanceResult.Rejected, await scenario.AcceptAsync(message));
        using var relay = Relay(scenario);
        await relay.RunOnceAsync(Cancellation);
        await using var context = scenario.Context();
        var failure = await context.Failures.SingleAsync(Cancellation);
        Assert.NotNull(failure.PublishedAt);
        await using var connection = await broker.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Cancellation);
        var reference = await channel.BasicGetAsync(RabbitMqFixture.FailuresQueue, true, Cancellation);
        Assert.NotNull(reference);
        Assert.DoesNotContain(message.Payload.Ciphertext, Encoding.UTF8.GetString(reference.Body.Span));
    }

    [Fact]
    public async Task Independent_worker_processes_accept_concurrent_duplicates_once_and_ack_all_redeliveries()
    {
        using var keys = new NotificationTestKeys();
        await PurgeAsync();
        await NotificationDatabaseScenario.ResetAsync(database);
        var message = keys.Message();
        for (var i = 0; i < 12; i++) { await PublishAsync(EnvelopeCodec.Encode(message)); }
        using var first = StartWorker(keys);
        using var second = StartWorker(keys);
        var output = new[] { first.StandardOutput.ReadToEndAsync(Cancellation), second.StandardOutput.ReadToEndAsync(Cancellation),
            first.StandardError.ReadToEndAsync(Cancellation), second.StandardError.ReadToEndAsync(Cancellation) };
        try
        {
            await WaitAsync(async () =>
            {
                await using var context = NotificationDatabaseScenario.Context(database);
                return await context.Inbox.AnyAsync(row => row.MessageId == message.MessageId && row.State == InboxState.Delivered, Cancellation);
            });
            await WaitForEmptyMainQueueAsync();
        }
        finally { await StopAsync(first); await StopAsync(second); }
        var logs = string.Join('\n', await Task.WhenAll(output));
        Assert.DoesNotContain("private-test-token", logs);
        Assert.DoesNotContain(message.Payload.Ciphertext, logs);
        await using var verify = NotificationDatabaseScenario.Context(database);
        var row = await verify.Inbox.SingleAsync(Cancellation);
        Assert.Equal(1, row.Attempts);
        Assert.Equal(InboxState.Delivered, row.State);
        Assert.Null(row.ProtectedEnvelope);
        Assert.Single(Directory.GetFiles(keys.EmailsPath, "*.eml"));
        await using var connection = await broker.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Cancellation);
        Assert.Null(await channel.BasicGetAsync(RabbitMqFixture.Queue, true, Cancellation));
    }

    [Fact]
    public async Task Late_failure_reference_uses_remaining_retention_and_broker_and_database_both_expire_it()
    {
        await PurgeAsync();
        await using var scenario = await NotificationDatabaseScenario.CreateAsync(database);
        var now = DateTimeOffset.UtcNow;
        var expiry = now.AddDays(-30).AddSeconds(8);
        var first = scenario.Message(occurred: expiry.AddHours(-1), expires: expiry) with { Version = 2 };
        var second = scenario.Message(occurred: expiry.AddHours(-1), expires: expiry) with { Version = 2 };
        await scenario.AcceptAsync(first);
        await scenario.AcceptAsync(second);
        using var relay = Relay(scenario);
        await relay.RunOnceAsync(Cancellation);
        await using var connection = await broker.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Cancellation);
        var reference = await channel.BasicGetAsync(RabbitMqFixture.FailuresQueue, true, Cancellation);
        Assert.NotNull(reference);
        Assert.InRange(long.Parse(reference.BasicProperties.Expiration!, System.Globalization.CultureInfo.InvariantCulture), 1, 8000);
        var wait = expiry.AddDays(30).AddSeconds(1) - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero) { await Task.Delay(wait, Cancellation); }
        Assert.Null(await channel.BasicGetAsync(RabbitMqFixture.FailuresQueue, true, Cancellation));
        await using var context = scenario.Context();
        await InboxPersistence.MaintainAsync(context, expiry.AddDays(30).AddSeconds(1), Cancellation);
        Assert.Equal(0, await context.Failures.CountAsync(Cancellation));
        Assert.Equal(0, await context.Inbox.CountAsync(Cancellation));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Database_failure_before_durable_acceptance_or_classification_does_not_ack_and_real_process_recovers(bool invalidContract)
    {
        using var keys = new NotificationTestKeys();
        await PurgeAsync();
        await NotificationDatabaseScenario.ResetAsync(database);
        var message = keys.Message();
        if (invalidContract) { message = message with { Version = 2 }; }
        var target = invalidContract ? "failure_outbox" : "inbox";
        await database.ExecuteAdminAsync(ProvisionedInstance.NotificationsDatabase, $"""
            CREATE FUNCTION notifications.block_acceptance() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected-durable-acceptance-failure'; END $$;
            CREATE TRIGGER block_acceptance BEFORE INSERT ON notifications.{target}
            FOR EACH ROW EXECUTE FUNCTION notifications.block_acceptance();
            """);
        var previousLogLength = (await database.LogsAsync()).Length;
        using (var failed = StartWorker(keys))
        {
            var output = failed.StandardOutput.ReadToEndAsync(Cancellation);
            var error = failed.StandardError.ReadToEndAsync(Cancellation);
            try
            {
                await PublishAsync(EnvelopeCodec.Encode(message));
                await WaitAsync(async () =>
                {
                    var logs = await database.LogsAsync();
                    return logs[Math.Min(previousLogLength, logs.Length)..].Contains("ERROR:  injected-durable-acceptance-failure", StringComparison.Ordinal);
                });
            }
            finally { await StopAsync(failed); }
            var logs = await output + await error;
            Assert.DoesNotContain("private-test-token", logs);
            Assert.DoesNotContain(message.Payload.Ciphertext, logs);
            await using var context = NotificationDatabaseScenario.Context(database);
            Assert.Equal(0, await context.Inbox.CountAsync(Cancellation));
            Assert.Equal(0, await context.Failures.CountAsync(Cancellation));
        }
        await using (var connection = await broker.ConnectAsync())
        await using (var channel = await connection.CreateChannelAsync(cancellationToken: Cancellation))
        {
            await WaitAsync(async () =>
            {
                var queue = await channel.QueueDeclarePassiveAsync(RabbitMqFixture.Queue, Cancellation);
                return queue.ConsumerCount == 0 && queue.MessageCount > 0;
            });
            var original = await channel.BasicGetAsync(RabbitMqFixture.Queue, false, Cancellation);
            Assert.NotNull(original);
            Assert.True(original.Redelivered);
            Assert.Equal(EnvelopeCodec.Encode(message), original.Body.ToArray());
            await channel.BasicNackAsync(original.DeliveryTag, false, true, Cancellation);
        }
        await database.ExecuteAdminAsync(ProvisionedInstance.NotificationsDatabase, $"DROP TRIGGER block_acceptance ON notifications.{target}");
        using var recovered = StartWorker(keys);
        var recoveredOutput = recovered.StandardOutput.ReadToEndAsync(Cancellation);
        var recoveredError = recovered.StandardError.ReadToEndAsync(Cancellation);
        try
        {
            await WaitAsync(async () =>
            {
                await using var context = NotificationDatabaseScenario.Context(database);
                return await context.Inbox.AnyAsync(row => row.MessageId == message.MessageId
                    && row.State == (invalidContract ? InboxState.Failed : InboxState.Delivered), Cancellation);
            });
            await WaitForEmptyMainQueueAsync();
        }
        finally { await StopAsync(recovered); }
        Assert.DoesNotContain("private-test-token", await recoveredOutput + await recoveredError);
        Assert.Equal(!invalidContract, Directory.Exists(keys.EmailsPath));
    }

    [Fact]
    public async Task Lost_ack_after_acceptance_commit_redelivers_but_does_not_create_or_send_a_second_work_item()
    {
        await PurgeAsync();
        await using var scenario = await NotificationDatabaseScenario.CreateAsync(database);
        var message = scenario.Message();
        await PublishAsync(EnvelopeCodec.Encode(message));
        await using (var connection = await broker.ConnectAsync("notifications_consumer", broker.ConsumerPassword))
        await using (var channel = await connection.CreateChannelAsync(cancellationToken: Cancellation))
        {
            var original = await channel.BasicGetAsync(RabbitMqFixture.Queue, false, Cancellation);
            Assert.NotNull(original);
            Assert.Equal(AcceptanceResult.Accepted, await scenario.AcceptAsync(original.Body.ToArray()));
            // Fecha conexão sem ACK, depois do commit no banco próprio.
        }
        using var worker = StartWorker(scenario.Keys);
        var output = worker.StandardOutput.ReadToEndAsync(Cancellation);
        var error = worker.StandardError.ReadToEndAsync(Cancellation);
        try
        {
            await WaitAsync(async () => (await scenario.RowAsync(message.MessageId)).State == InboxState.Delivered);
            await WaitForEmptyMainQueueAsync();
        }
        finally { await StopAsync(worker); }
        Assert.DoesNotContain("private-test-token", await output + await error);
        await using var context = scenario.Context();
        Assert.Single(await context.Inbox.ToListAsync(Cancellation));
        Assert.Equal(1, (await scenario.RowAsync(message.MessageId)).Attempts);
        Assert.Single(Directory.GetFiles(scenario.Keys.EmailsPath, "*.eml"));
    }

    [Fact]
    public async Task Failure_reference_is_confirmed_with_separate_credential_sanitized_body_and_remaining_ttl()
    {
        await PurgeAsync();
        await using var scenario = await NotificationDatabaseScenario.CreateAsync(database);
        var message = scenario.Message() with { Version = 2 };
        await scenario.AcceptAsync(message);
        using var relay = Relay(scenario);
        await relay.RunOnceAsync(Cancellation);
        await using var context = scenario.Context();
        var failure = await context.Failures.SingleAsync(Cancellation);
        Assert.NotNull(failure.PublishedAt);
        await using var connection = await broker.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Cancellation);
        var delivery = await channel.BasicGetAsync(RabbitMqFixture.FailuresQueue, true, Cancellation);
        Assert.NotNull(delivery);
        using var json = JsonDocument.Parse(delivery.Body);
        Assert.Equal(new[] { "code", "failureId", "messageId", "occurredAt", "retainUntil", "type", "version" },
            json.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        var text = Encoding.UTF8.GetString(delivery.Body.Span);
        Assert.DoesNotContain(message.Payload.Ciphertext, text);
        Assert.DoesNotContain("private-test-token", text);
        Assert.DoesNotContain(NotificationTestKeys.Content.Recipient, text);
        Assert.True(delivery.BasicProperties.Persistent);
        Assert.Equal(failure.Id.ToString("D"), delivery.BasicProperties.MessageId);
        Assert.InRange(long.Parse(delivery.BasicProperties.Expiration!, System.Globalization.CultureInfo.InvariantCulture), 1, 2_592_000_000);
        // Credencial de consumo permanece sem publicação; esta publicação usa notifications_failures.
        Assert.Equal("unsupported-version", json.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Failure_relay_survives_broker_outage_and_missing_route_suspends_until_audited_resume()
    {
        await PurgeAsync();
        await using var scenario = await NotificationDatabaseScenario.CreateAsync(database);
        await scenario.AcceptAsync(scenario.Message() with { Version = 2 });
        using var down = Relay(scenario);
        await broker.StopAsync();
        try { await down.RunOnceAsync(Cancellation); }
        finally { await broker.StartAsync(); }
        using var relay = Relay(scenario);
        await using (var context = scenario.Context())
        {
            var pending = await context.Failures.SingleAsync(Cancellation);
            Assert.Null(pending.PublishedAt);
            Assert.False(pending.Suspended);
            Assert.NotNull(pending.NextAttemptAt);
        }
        await database.ExecuteAdminAsync(ProvisionedInstance.NotificationsDatabase, "UPDATE notifications.failure_outbox SET next_attempt_at=NULL");
        await using var connection = await broker.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Cancellation);
        await channel.QueueUnbindAsync(RabbitMqFixture.FailuresQueue, RabbitMqFixture.FailuresExchange, "email.failed.v1", cancellationToken: Cancellation);
        try { await relay.RunOnceAsync(Cancellation); }
        finally { await channel.QueueBindAsync(RabbitMqFixture.FailuresQueue, RabbitMqFixture.FailuresExchange, "email.failed.v1", cancellationToken: Cancellation); }
        await using var verify = scenario.Context();
        var suspended = await verify.Failures.AsNoTracking().SingleAsync(Cancellation);
        Assert.True(suspended.Suspended);
        await relay.RunOnceAsync(Cancellation);
        Assert.Null((await verify.Failures.AsNoTracking().SingleAsync(Cancellation)).PublishedAt);
        Assert.True((await database.MessagingRecoveryAsync("resume-notification-failure-publication.sql", "failure_id", suspended.Id)).ExitCode == 0);
        await relay.RunOnceAsync(Cancellation);
        Assert.NotNull((await verify.Failures.AsNoTracking().SingleAsync(Cancellation)).PublishedAt);
        Assert.Equal(1, await verify.Recoveries.CountAsync(Cancellation));
    }

    [Fact]
    public async Task Failure_reference_confirm_without_local_progress_can_republish_same_identity_without_original_payload()
    {
        await PurgeAsync();
        await using var scenario = await NotificationDatabaseScenario.CreateAsync(database);
        await scenario.AcceptAsync(scenario.Message() with { Version = 2 });
        var lease = Guid.NewGuid();
        await using (var scope = scenario.Services.CreateAsyncScope())
        {
            var row = Assert.Single(await scope.ServiceProvider.GetRequiredService<FailureDispatchStore>().ClaimAsync(lease, DateTimeOffset.UtcNow, Cancellation));
            await using var connection = await broker.ConnectAsync("notifications_failures", broker.FailuresPassword);
            await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(true, true), Cancellation);
            await channel.BasicPublishAsync(RabbitMqFixture.FailuresExchange, "email.failed.v1", true,
                new BasicProperties { Persistent = true, MessageId = row.Id.ToString("D") },
                EnvelopeCodec.EncodeFailure(new MessageFailureReference(row.Id, row.MessageId, row.Code, row.OccurredAt, row.RetainUntil), row.OriginalExpiresAt), Cancellation);
            // Sem ConfirmAsync: a referência permanece recuperável no banco.
        }
        await database.ExecuteAdminAsync(ProvisionedInstance.NotificationsDatabase, "UPDATE notifications.failure_outbox SET lease_until=now()-interval '1 second'");
        using var restarted = Relay(scenario);
        await restarted.RunOnceAsync(Cancellation);
        await using var read = await broker.ConnectAsync();
        await using var consumer = await read.CreateChannelAsync(cancellationToken: Cancellation);
        var first = await consumer.BasicGetAsync(RabbitMqFixture.FailuresQueue, true, Cancellation);
        var duplicate = await consumer.BasicGetAsync(RabbitMqFixture.FailuresQueue, true, Cancellation);
        Assert.NotNull(first);
        Assert.NotNull(duplicate);
        Assert.Equal(first.BasicProperties.MessageId, duplicate.BasicProperties.MessageId);
        Assert.Equal(first.Body.ToArray(), duplicate.Body.ToArray());
    }

    private FailureReferenceRelay Relay(NotificationDatabaseScenario scenario) => new(scenario.Services.GetRequiredService<IServiceScopeFactory>(),
        Options.Create(new NotificationOptions
        {
            FailuresBroker = new BrokerSettings
            { Host = broker.Host, Port = broker.Port, User = "notifications_failures", Password = broker.FailuresPassword }
        }),
        scenario.Services.GetRequiredService<IHostEnvironment>(), TimeProvider.System, NullLogger<FailureReferenceRelay>.Instance);

    private async Task WaitForEmptyMainQueueAsync()
    {
        // As estatísticas da gestão são atualizadas periodicamente e podem mostrar 0 antes de a publicação
        // aparecer; a contagem de prontas pela AMQP é imediata. A gestão cobre também as não reconhecidas.
        await using (var connection = await broker.ConnectAsync())
        await using (var channel = await connection.CreateChannelAsync(cancellationToken: Cancellation))
        {
            await WaitAsync(async () => (await channel.QueueDeclarePassiveAsync(RabbitMqFixture.Queue, Cancellation)).MessageCount == 0);
        }
        using var client = broker.ManagementClient();
        await WaitAsync(async () =>
        {
            using var response = await client.GetAsync("queues/ouroboros/notifications.email.v1", Cancellation);
            response.EnsureSuccessStatusCode();
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(Cancellation), cancellationToken: Cancellation);
            return json.RootElement.TryGetProperty("messages", out var count) && count.GetInt64() == 0;
        });
    }

    private static async Task StopAsync(Process process)
    {
        if (!process.HasExited) { process.Kill(entireProcessTree: true); }
        await process.WaitForExitAsync(Cancellation);
    }
}
