using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ouroboros.Messaging;
using Ouroboros.Notifications.Application;
using Ouroboros.Notifications.Contracts;
using Ouroboros.Notifications.Domain;
using Ouroboros.Notifications.Infrastructure;
using Ouroboros.Notifications.Infrastructure.Persistence;

namespace Ouroboros.IntegrationTests;

public sealed class NotificationInboxTests(ProvisionedInstance database) : IClassFixture<ProvisionedInstance>
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Newer_expired_message_cancels_unsent_predecessor_and_preserves_supersession_cursor()
    {
        var transport = new CountingTransport();
        await using var scenario = await NotificationDatabaseScenario.CreateAsync(database, transport);
        var now = scenario.Clock.GetUtcNow();
        var previous = scenario.Message("expired-successor", occurred: now.AddHours(-2), expires: now.AddHours(2));
        var latest = scenario.Message("expired-successor", occurred: now.AddHours(-1), expires: now.AddMinutes(-1));
        Assert.Equal(AcceptanceResult.Accepted, await scenario.AcceptAsync(previous));
        Assert.Equal(AcceptanceResult.Expired, await scenario.AcceptAsync(latest));
        var cancelled = await scenario.RowAsync(previous.MessageId);
        Assert.Equal(InboxState.Cancelled, cancelled.State);
        Assert.Null(cancelled.ProtectedEnvelope);
        var intermediate = scenario.Message("expired-successor", occurred: now.AddMinutes(-90), expires: now.AddHours(1));
        Assert.Equal(AcceptanceResult.Superseded, await scenario.AcceptAsync(intermediate));
        await scenario.RunAsync();
        Assert.Equal(0, transport.Calls);
        Assert.Equal(InboxState.Expired, (await scenario.RowAsync(latest.MessageId)).State);
    }

    [Fact]
    public async Task Protected_postgres_backup_restores_inbox_and_terminal_states_then_reapplies_expiration_before_processing()
    {
        var transport = new CountingTransport();
        await using var scenario = await NotificationDatabaseScenario.CreateAsync(database, transport);
        var completed = scenario.Message();
        await scenario.AcceptAsync(completed);
        await scenario.RunAsync();
        var pending = scenario.Message();
        await scenario.AcceptAsync(pending);
        var restored = await database.RestoreProtectedNotificationsBackupAsync();
        try
        {
            await using var context = new NotificationsDbContext(new DbContextOptionsBuilder<NotificationsDbContext>()
                .UseNpgsql(database.ConnectionStringFor("postgres", restored), pg => pg.MigrationsHistoryTable("__EFMigrationsHistory", "notifications")).Options);
            var accepted = await context.Inbox.AsNoTracking().SingleAsync(row => row.MessageId == pending.MessageId, Cancellation);
            Assert.Equal(InboxState.Accepted, accepted.State);
            Assert.DoesNotContain("private-test-token", Encoding.UTF8.GetString(accepted.ProtectedEnvelope!));
            Assert.True(EnvelopeCodec.TryDecode(accepted.ProtectedEnvelope!, "notification.email.requested", 1, "identity", out var envelope, out _));
            var content = scenario.Services.GetRequiredService<IEmailPayloadReader>().Read(envelope!);
            Assert.Equal(NotificationTestKeys.Content.Text, content.Text);
            Assert.Equal(InboxState.Delivered, (await context.Inbox.AsNoTracking().SingleAsync(row => row.MessageId == completed.MessageId, Cancellation)).State);
            await InboxPersistence.MaintainAsync(context, accepted.ExpiresAt, Cancellation);
            Assert.Null((await context.Inbox.AsNoTracking().SingleAsync(row => row.MessageId == pending.MessageId, Cancellation)).ProtectedEnvelope);
            Assert.Equal(InboxState.Delivered, (await context.Inbox.AsNoTracking().SingleAsync(row => row.MessageId == completed.MessageId, Cancellation)).State);
            Assert.Equal(1, transport.Calls);
        }
        finally { Npgsql.NpgsqlConnection.ClearAllPools(); await database.DropRestoredNotificationsAsync(restored); }
    }

    [Fact]
    public async Task Old_executor_cannot_begin_or_complete_another_lease_and_new_acceptance_cancels_claimed_predecessor()
    {
        var transport = new CountingTransport();
        await using var scenario = await NotificationDatabaseScenario.CreateAsync(database, transport);
        var message = scenario.Message("fenced");
        await scenario.AcceptAsync(message);
        await using var oldScope = scenario.Services.CreateAsyncScope();
        var oldStore = oldScope.ServiceProvider.GetRequiredService<InboxExecutionStore>();
        var oldLease = Guid.NewGuid();
        Assert.Single(await oldStore.ClaimAsync(oldLease, scenario.Clock.GetUtcNow(), Cancellation));
        scenario.Clock.Advance(TimeSpan.FromSeconds(70));
        await using var newScope = scenario.Services.CreateAsyncScope();
        var newStore = newScope.ServiceProvider.GetRequiredService<InboxExecutionStore>();
        var newLease = Guid.NewGuid();
        Assert.Single(await newStore.ClaimAsync(newLease, scenario.Clock.GetUtcNow(), Cancellation));
        Assert.Equal(0, await oldStore.BeginAttemptAsync(message.MessageId, oldLease, scenario.Clock.GetUtcNow(), scenario.Clock.GetUtcNow().AddSeconds(30), Cancellation));
        Assert.Equal(0, await oldStore.DeliverAsync(message.MessageId, oldLease, scenario.Clock.GetUtcNow(), Cancellation));
        var replacement = scenario.Message("fenced");
        await scenario.AcceptAsync(replacement);
        Assert.False(await newStore.IsOwnedAsync(message.MessageId, newLease, scenario.Clock.GetUtcNow(), Cancellation));
        Assert.Equal(0, await newStore.BeginAttemptAsync(message.MessageId, newLease, scenario.Clock.GetUtcNow(), scenario.Clock.GetUtcNow().AddSeconds(30), Cancellation));
        await scenario.RunAsync();
        Assert.Equal(InboxState.Cancelled, (await scenario.RowAsync(message.MessageId)).State);
        Assert.Equal(InboxState.Delivered, (await scenario.RowAsync(replacement.MessageId)).State);
        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task Conflict_after_delivery_does_not_change_completed_work_or_send_the_conflicting_content()
    {
        var transport = new CountingTransport();
        await using var scenario = await NotificationDatabaseScenario.CreateAsync(database, transport);
        var message = scenario.Message();
        await scenario.AcceptAsync(message);
        await scenario.RunAsync();
        var before = await scenario.RowAsync(message.MessageId);
        Assert.Equal(AcceptanceResult.Conflict, await scenario.AcceptAsync(scenario.Message(id: message.MessageId)));
        await scenario.RunAsync();
        var after = await scenario.RowAsync(message.MessageId);
        Assert.Equal(InboxState.Delivered, after.State);
        Assert.Equal(before.Fingerprint, after.Fingerprint);
        Assert.Equal(before.Attempts, after.Attempts);
        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task Optional_unknown_fields_preserve_the_same_canonical_fingerprint()
    {
        await using var scenario = await NotificationDatabaseScenario.CreateAsync(database);
        var message = scenario.Message();
        await scenario.AcceptAsync(message);
        var json = System.Text.Json.Nodes.JsonNode.Parse(EnvelopeCodec.Encode(message))!.AsObject();
        json["futureOptionalField"] = "ignored";
        Assert.Equal(AcceptanceResult.Duplicate, await scenario.AcceptAsync(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }

    [Fact]
    public async Task Expiration_cleanup_works_without_executor_and_late_redelivery_does_not_send_after_tombstone_purge()
    {
        var transport = new CountingTransport();
        await using var scenario = await NotificationDatabaseScenario.CreateAsync(database, transport);
        var message = scenario.Message(expires: scenario.Clock.GetUtcNow().AddMinutes(1));
        await scenario.AcceptAsync(message);
        scenario.Clock.Advance(TimeSpan.FromMinutes(2));
        await using (var context = scenario.Context()) { await InboxPersistence.MaintainAsync(context, scenario.Clock.GetUtcNow(), Cancellation); }
        var expired = await scenario.RowAsync(message.MessageId);
        Assert.Equal(InboxState.Expired, expired.State);
        Assert.Null(expired.ProtectedEnvelope);
        scenario.Clock.SetUtcNow(message.ExpiresAt.AddDays(30).AddMilliseconds(1));
        await using (var context = scenario.Context())
        {
            await InboxPersistence.MaintainAsync(context, scenario.Clock.GetUtcNow(), Cancellation);
            Assert.Equal(0, await context.Inbox.CountAsync(Cancellation));
            Assert.Equal(0, await context.Supersessions.CountAsync(Cancellation));
        }
        Assert.Equal(AcceptanceResult.Expired, await scenario.AcceptAsync(message));
        await scenario.RunAsync();
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task Wrong_private_key_suspends_without_spending_provider_attempt_and_explicit_recovery_is_audited()
    {
        var transport = new CountingTransport();
        await using var scenario = await NotificationDatabaseScenario.CreateAsync(database, transport);
        var message = scenario.Message();
        await scenario.AcceptAsync(message);
        var keyPath = Path.Combine(scenario.Keys.DirectoryPath, "key.pem");
        var original = await File.ReadAllTextAsync(keyPath, Cancellation);
        using (var incorrect = RSA.Create(3072)) { await File.WriteAllTextAsync(keyPath, incorrect.ExportPkcs8PrivateKeyPem(), Cancellation); }
        await scenario.RunAsync();
        var suspended = await scenario.RowAsync(message.MessageId);
        Assert.True(suspended.Suspended);
        Assert.Equal(0, suspended.Attempts);
        Assert.NotNull(suspended.ProtectedEnvelope);
        Assert.Equal(0, transport.Calls);
        await File.WriteAllTextAsync(keyPath, original, Cancellation);
        await scenario.RunAsync();
        Assert.Equal(0, transport.Calls);
        var recovery = await database.MessagingRecoveryAsync("resume-notification-processing.sql", "message_id", message.MessageId);
        Assert.True(recovery.ExitCode == 0, "Recuperação explícita falhou.");
        await scenario.RunAsync();
        Assert.Equal(1, transport.Calls);
        var delivered = await scenario.RowAsync(message.MessageId);
        Assert.Equal(InboxState.Delivered, delivered.State);
        Assert.Equal(suspended.ExpiresAt, delivered.ExpiresAt);
        await using var context = scenario.Context();
        Assert.Equal(1, await context.Recoveries.CountAsync(Cancellation));
        await database.MessagingRecoveryAsync("resume-notification-processing.sql", "message_id", message.MessageId);
        Assert.Equal(1, await context.Recoveries.CountAsync(Cancellation));
        Assert.Equal(1, (await scenario.RowAsync(message.MessageId)).Attempts);
    }

    [Fact]
    public async Task Ambiguous_provider_result_after_crash_can_send_again_while_pickup_keeps_a_single_stable_artifact()
    {
        var transport = new CountingTransport();
        await using var scenario = await NotificationDatabaseScenario.CreateAsync(database, transport);
        var message = scenario.Message();
        await scenario.AcceptAsync(message);
        await SimulateSendWithoutProgressAsync(scenario, transport, message.MessageId);
        Assert.Equal(1, transport.Calls);
        scenario.Clock.Advance(TimeSpan.FromSeconds(70));
        await scenario.RestartAsync();
        await scenario.RunAsync();
        Assert.Equal(2, transport.Calls); // provedor sem idempotência pode repetir o efeito externo
        Assert.Equal(2, (await scenario.RowAsync(message.MessageId)).Attempts);
        Assert.Equal(InboxState.Delivered, (await scenario.RowAsync(message.MessageId)).State);
    }

    [Fact]
    public async Task Pickup_recovery_after_send_without_local_confirmation_does_not_rewrite_the_same_eml()
    {
        await using var scenario = await NotificationDatabaseScenario.CreateAsync(database);
        var message = scenario.Message();
        await scenario.AcceptAsync(message);
        await SimulateSendWithoutProgressAsync(scenario, scenario.Services.GetRequiredService<IEmailTransport>(), message.MessageId);
        var path = Assert.Single(Directory.GetFiles(scenario.Keys.EmailsPath, "*.eml"));
        var before = await File.ReadAllBytesAsync(path, Cancellation);
        var written = File.GetLastWriteTimeUtc(path);
        scenario.Clock.Advance(TimeSpan.FromSeconds(70));
        await scenario.RestartAsync();
        await scenario.RunAsync();
        Assert.Single(Directory.GetFiles(scenario.Keys.EmailsPath, "*.eml"));
        Assert.Equal(before, await File.ReadAllBytesAsync(path, Cancellation));
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
        Assert.Equal(InboxState.Delivered, (await scenario.RowAsync(message.MessageId)).State);
    }

    [Fact]
    public async Task Retry_after_is_clamped_to_deadline_and_no_provider_call_begins_after_expiration()
    {
        var transport = new CountingTransport { Failure = () => new NotificationDeliveryException(DeliveryFailure.Transient, TimeSpan.MaxValue) };
        await using var scenario = await NotificationDatabaseScenario.CreateAsync(database, transport);
        var message = scenario.Message(expires: scenario.Clock.GetUtcNow().AddMinutes(10));
        await scenario.AcceptAsync(message);
        await scenario.RunAsync();
        var row = await scenario.RowAsync(message.MessageId);
        Assert.Equal(row.ExpiresAt, row.NextAttemptAt);
        scenario.Clock.Advance(TimeSpan.FromMinutes(11));
        await scenario.RunAsync();
        Assert.Equal(InboxState.Expired, (await scenario.RowAsync(message.MessageId)).State);
        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task Fifth_attempt_crash_is_recovered_as_exhausted_without_a_sixth_call()
    {
        var transport = new CountingTransport();
        await using var scenario = await NotificationDatabaseScenario.CreateAsync(database, transport);
        var message = scenario.Message();
        await scenario.AcceptAsync(message);
        await database.ExecuteAdminAsync(ProvisionedInstance.NotificationsDatabase, "UPDATE notifications.inbox SET attempts=4");
        await SimulateSendWithoutProgressAsync(scenario, transport, message.MessageId);
        scenario.Clock.Advance(TimeSpan.FromSeconds(70));
        await scenario.RunAsync();
        var terminal = await scenario.RowAsync(message.MessageId);
        Assert.Equal(5, terminal.Attempts);
        Assert.Equal(InboxState.Failed, terminal.State);
        Assert.Equal("retry-exhausted", terminal.LastFailureCode);
        Assert.Null(terminal.ProtectedEnvelope);
        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task Failed_durable_classification_rolls_back_both_inbox_and_failure_outbox()
    {
        await using var scenario = await NotificationDatabaseScenario.CreateAsync(database);
        await database.ExecuteAdminAsync(ProvisionedInstance.NotificationsDatabase, """
            CREATE FUNCTION notifications.reject_failure() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected classification failure'; END $$;
            CREATE TRIGGER reject_failure BEFORE INSERT ON notifications.failure_outbox
            FOR EACH ROW EXECUTE FUNCTION notifications.reject_failure();
            """);
        await Assert.ThrowsAsync<DbUpdateException>(() => scenario.AcceptAsync(scenario.Message() with { Version = 2 }));
        await using var context = scenario.Context();
        Assert.Equal(0, await context.Inbox.CountAsync(Cancellation));
        Assert.Equal(0, await context.Failures.CountAsync(Cancellation));
    }

    [Fact]
    public async Task Runtime_cannot_read_identity_or_mutate_migrations_and_the_service_model_is_isolated()
    {
        await using var scenario = await NotificationDatabaseScenario.CreateAsync(database);
        await using var foreign = database.Connect(ProvisionedInstance.NotificationsRuntime, ProvisionedInstance.IdentityDatabase);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => foreign.OpenAsync(Cancellation));
        await using var owned = database.Connect(ProvisionedInstance.NotificationsRuntime, ProvisionedInstance.NotificationsDatabase);
        await owned.OpenAsync(Cancellation);
        await using var history = new Npgsql.NpgsqlCommand("SELECT * FROM notifications.\"__EFMigrationsHistory\"", owned);
        Assert.Equal("42501", (await Assert.ThrowsAsync<Npgsql.PostgresException>(() => history.ExecuteNonQueryAsync(Cancellation))).SqlState);
        await using var ddl = new Npgsql.NpgsqlCommand("CREATE TABLE notifications.forbidden (id int)", owned);
        Assert.Equal("42501", (await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ddl.ExecuteNonQueryAsync(Cancellation))).SqlState);
        await using var context = scenario.Context();
        Assert.False(context.Database.HasPendingModelChanges());
        Assert.All(context.Model.GetEntityTypes(), entity => Assert.Equal("notifications", entity.GetSchema()));
    }

    private static async Task SimulateSendWithoutProgressAsync(NotificationDatabaseScenario scenario, IEmailTransport transport, Guid id)
    {
        await using var scope = scenario.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<InboxExecutionStore>();
        var lease = Guid.NewGuid();
        var row = Assert.Single(await store.ClaimAsync(lease, scenario.Clock.GetUtcNow(), Cancellation));
        Assert.Equal(id, row.MessageId);
        Assert.Equal(1, await store.BeginAttemptAsync(id, lease, scenario.Clock.GetUtcNow(), scenario.Clock.GetUtcNow().AddSeconds(30), Cancellation));
        Assert.True(EnvelopeCodec.TryDecode(row.ProtectedEnvelope!, "notification.email.requested", 1, "identity", out var message, out _));
        var content = scope.ServiceProvider.GetRequiredService<IEmailPayloadReader>().Read(message!);
        await transport.SendAsync(id, content, Cancellation);
        // Simula queda após o efeito externo, antes de DeliverAsync; nada em memória completa o estado.
    }

    [Fact]
    public async Task Concurrent_duplicates_commit_one_protected_inbox_and_one_provider_attempt_and_do_not_restart_delivered_work()
    {
        var transport = new CountingTransport();
        await using var scenario = await NotificationDatabaseScenario.CreateAsync(database, transport);
        var message = scenario.Message();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => scenario.AcceptAsync(message)));
        Assert.Equal(1, results.Count(result => result == AcceptanceResult.Accepted));
        Assert.Equal(7, results.Count(result => result == AcceptanceResult.Duplicate));
        var accepted = await scenario.RowAsync(message.MessageId);
        Assert.Equal(0, accepted.Attempts);
        Assert.DoesNotContain(NotificationTestKeys.Content.Recipient, Encoding.UTF8.GetString(accepted.ProtectedEnvelope!));
        Assert.DoesNotContain("private-test-token", Encoding.UTF8.GetString(accepted.ProtectedEnvelope!));
        Assert.NotEqual(SHA256.HashData(EnvelopeCodec.Encode(message)), accepted.Fingerprint);
        await Task.WhenAll(scenario.RunAsync(), scenario.RunAsync());
        Assert.Equal(1, transport.Calls);
        var delivered = await scenario.RowAsync(message.MessageId);
        Assert.Equal(InboxState.Delivered, delivered.State);
        Assert.Equal(1, delivered.Attempts);
        Assert.Null(delivered.ProtectedEnvelope);
        Assert.Null(delivered.SupersessionId);
        Assert.Null(delivered.LeaseId);
        Assert.Equal(AcceptanceResult.Duplicate, await scenario.AcceptAsync(message));
        await scenario.RestartAsync();
        await scenario.RunAsync();
        Assert.Equal(1, transport.Calls);
        await using var context = scenario.Context();
        Assert.Equal(1, await context.Inbox.CountAsync(Cancellation));
        Assert.Equal(0, await context.Failures.CountAsync(Cancellation));
    }

    [Fact]
    public async Task Content_conflict_quarantines_unsent_identity_without_overwriting_fingerprint_or_reopening_on_original_duplicate()
    {
        var transport = new CountingTransport();
        await using var scenario = await NotificationDatabaseScenario.CreateAsync(database, transport);
        var original = scenario.Message();
        await scenario.AcceptAsync(original);
        var accepted = await scenario.RowAsync(original.MessageId);
        var conflicting = scenario.Message(id: original.MessageId);
        Assert.Equal(AcceptanceResult.Conflict, await scenario.AcceptAsync(conflicting));
        Assert.Equal(AcceptanceResult.Conflict, await scenario.AcceptAsync(conflicting));
        Assert.Equal(AcceptanceResult.Duplicate, await scenario.AcceptAsync(original));
        await scenario.RunAsync();
        var row = await scenario.RowAsync(original.MessageId);
        Assert.Equal(InboxState.Failed, row.State);
        Assert.Equal("content-conflict", row.LastFailureCode);
        Assert.Equal(accepted.Fingerprint, row.Fingerprint);
        Assert.Null(row.ProtectedEnvelope);
        Assert.Equal(0, transport.Calls);
        await using var context = scenario.Context();
        Assert.Single(await context.Failures.ToListAsync(Cancellation));
    }

    [Fact]
    public async Task Later_accepted_message_cancels_unsent_predecessor_and_out_of_order_older_message_is_discarded()
    {
        var transport = new CountingTransport();
        await using var scenario = await NotificationDatabaseScenario.CreateAsync(database, transport);
        var first = scenario.Message("same-alias", occurred: scenario.Clock.GetUtcNow().AddSeconds(-3));
        var latest = scenario.Message("same-alias", occurred: scenario.Clock.GetUtcNow().AddSeconds(-1));
        var middle = scenario.Message("same-alias", occurred: scenario.Clock.GetUtcNow().AddSeconds(-2));
        await scenario.AcceptAsync(first);
        await scenario.AcceptAsync(latest);
        Assert.Equal(AcceptanceResult.Superseded, await scenario.AcceptAsync(middle));
        Assert.Equal(InboxState.Cancelled, (await scenario.RowAsync(first.MessageId)).State);
        Assert.Null((await scenario.RowAsync(first.MessageId)).ProtectedEnvelope);
        Assert.Equal(InboxState.Cancelled, (await scenario.RowAsync(middle.MessageId)).State);
        await scenario.RunAsync();
        Assert.Equal(1, transport.Calls);
        Assert.Equal(InboxState.Delivered, (await scenario.RowAsync(latest.MessageId)).State);
    }

    [Fact]
    public async Task Retries_are_persisted_and_limited_to_five_provider_calls_including_after_host_restart()
    {
        var transport = new CountingTransport { Failure = () => new IOException("injected-transient") };
        await using var scenario = await NotificationDatabaseScenario.CreateAsync(database, transport);
        var message = scenario.Message();
        await scenario.AcceptAsync(message);
        var seconds = new[] { 30, 120, 600, 1800 };
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            await scenario.RunAsync();
            var row = await scenario.RowAsync(message.MessageId);
            Assert.Equal(attempt, row.Attempts);
            Assert.Equal(attempt, transport.Calls);
            if (attempt < 5)
            {
                Assert.Equal(InboxState.Accepted, row.State);
                Assert.InRange((row.NextAttemptAt!.Value - scenario.Clock.GetUtcNow()).TotalSeconds, seconds[attempt - 1] * 0.8 - 0.001, seconds[attempt - 1] * 1.2 + 0.001);
                scenario.Clock.SetUtcNow(row.NextAttemptAt.Value.AddMilliseconds(1));
                if (attempt == 2) { await scenario.RestartAsync(); }
            }
        }
        var terminal = await scenario.RowAsync(message.MessageId);
        Assert.Equal(InboxState.Failed, terminal.State);
        Assert.Equal("retry-exhausted", terminal.LastFailureCode);
        Assert.Null(terminal.ProtectedEnvelope);
        await scenario.AcceptAsync(message);
        await scenario.RunAsync();
        Assert.Equal(5, transport.Calls);
        await using var context = scenario.Context();
        Assert.Equal("retry-exhausted", (await context.Failures.SingleAsync(Cancellation)).Code);
    }

    [Fact]
    public async Task Permanent_provider_failure_and_invalid_protected_content_have_durable_sanitized_terminal_classification()
    {
        var transport = new CountingTransport { Failure = () => new NotificationDeliveryException(DeliveryFailure.Permanent) };
        await using var scenario = await NotificationDatabaseScenario.CreateAsync(database, transport);
        var rejected = scenario.Message();
        await scenario.AcceptAsync(rejected);
        await scenario.RunAsync();
        Assert.Equal("provider-permanent", (await scenario.RowAsync(rejected.MessageId)).LastFailureCode);
        var tampered = scenario.Message();
        tampered = tampered with { Payload = tampered.Payload with { Tag = Convert.ToBase64String(new byte[16]) } };
        await scenario.AcceptAsync(tampered);
        await scenario.RunAsync();
        var invalid = await scenario.RowAsync(tampered.MessageId);
        Assert.Equal(InboxState.Failed, invalid.State);
        Assert.Equal("invalid-contract", invalid.LastFailureCode);
        Assert.Equal(0, invalid.Attempts);
        Assert.Null(invalid.ProtectedEnvelope);
        Assert.Equal(1, transport.Calls);
        await using var context = scenario.Context();
        Assert.Equal(2, await context.Failures.CountAsync(Cancellation));
    }

    [Fact]
    public async Task Invalid_or_unknown_contract_is_durably_rejected_without_storing_original_or_starting_provider()
    {
        var transport = new CountingTransport();
        await using var scenario = await NotificationDatabaseScenario.CreateAsync(database, transport);
        var unsupported = scenario.Message() with { Version = 2 };
        foreach (var body in new[] { EnvelopeCodec.Encode(unsupported), Encoding.UTF8.GetBytes("[]"), Encoding.UTF8.GetBytes("{\"body\":\"private-test-token\"}") })
        {
            Assert.Equal(AcceptanceResult.Rejected, await scenario.AcceptAsync(body));
            Assert.Equal(AcceptanceResult.Duplicate, await scenario.AcceptAsync(body));
        }
        await scenario.RunAsync();
        Assert.Equal(0, transport.Calls);
        await using var context = scenario.Context();
        Assert.Equal(3, await context.Inbox.CountAsync(row => row.State == InboxState.Failed && row.ProtectedEnvelope == null, Cancellation));
        Assert.Equal(3, await context.Failures.CountAsync(Cancellation));
    }

    [Fact]
    public async Task Fingerprint_rotation_uses_retained_key_to_deduplicate_existing_terminal_identity()
    {
        var transport = new CountingTransport();
        await using var scenario = await NotificationDatabaseScenario.CreateAsync(database, transport);
        var message = scenario.Message();
        await scenario.AcceptAsync(message);
        await scenario.RunAsync();
        scenario.Keys.RotateFingerprintKey("second");
        Assert.Equal(AcceptanceResult.Duplicate, await scenario.AcceptAsync(message));
        await scenario.RunAsync();
        Assert.Equal(1, transport.Calls);
        var fresh = scenario.Message();
        await scenario.AcceptAsync(fresh);
        Assert.Equal("second", (await scenario.RowAsync(fresh.MessageId)).FingerprintKeyId);
    }

    private sealed class CountingTransport : IEmailTransport
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        internal Func<Exception?>? Failure { get; init; }
        public Task SendAsync(Guid messageId, EmailContent content, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            if (Failure?.Invoke() is { } exception) { throw exception; }
            return Task.CompletedTask;
        }
    }
}
