using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Ouroboros.Messaging;
using Ouroboros.Messaging.Contracts;
using Ouroboros.Notifications;
using Ouroboros.Notifications.Application;
using Ouroboros.Notifications.Contracts;
using Ouroboros.Notifications.Infrastructure;
using Ouroboros.Notifications.Infrastructure.Persistence;
using RabbitMQ.Client;

namespace Ouroboros.IntegrationTests;

[Collection("Telemetry")]
public sealed partial class NotificationsWorkerTests(RabbitMqFixture broker, ProvisionedInstance database)
    : IClassFixture<RabbitMqFixture>, IClassFixture<ProvisionedInstance>
{
    [Fact]
    public async Task Protected_content_is_preserved_in_eml_and_repetition_keeps_stable_file()
    {
        using var keys = new NotificationTestKeys();
        using var host = Host(keys);
        using var scope = host.Services.CreateScope();
        var delivery = scope.ServiceProvider.GetRequiredService<INotificationDelivery>();
        var message = keys.Message();
        Assert.Equal(NotificationDeliveryResult.Delivered, await delivery.ExecuteAsync(message, TestContext.Current.CancellationToken));
        var path = Assert.Single(Directory.GetFiles(keys.EmailsPath, "*.eml"));
        var before = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        Assert.Contains("To: " + NotificationTestKeys.Content.Recipient, before);
        Assert.Contains(NotificationTestKeys.Content.Text.ReplaceLineEndings("\r\n"), before);
        Assert.Contains(message.MessageId.ToString("N"), Path.GetFileName(path));
        await delivery.ExecuteAsync(message, TestContext.Current.CancellationToken);
        Assert.Single(Directory.GetFiles(keys.EmailsPath, "*.eml"));
        Assert.Equal(before, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Metadata_or_content_tampering_is_rejected_without_eml_or_secret_exception()
    {
        using var keys = new NotificationTestKeys();
        using var host = Host(keys);
        using var scope = host.Services.CreateScope();
        var delivery = scope.ServiceProvider.GetRequiredService<INotificationDelivery>();
        var message = keys.Message();
        var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => delivery.ExecuteAsync(
            message with { ExpiresAt = message.ExpiresAt.AddMinutes(1) }, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(NotificationTestKeys.Content.Text, error.ToString());
        Assert.DoesNotContain(message.Payload.Ciphertext, error.ToString());
        Assert.False(Directory.Exists(keys.EmailsPath));
    }

    [Fact]
    public async Task Header_injection_expiry_and_unknown_version_do_not_send()
    {
        using var keys = new NotificationTestKeys();
        using var host = Host(keys);
        using var scope = host.Services.CreateScope();
        var delivery = scope.ServiceProvider.GetRequiredService<INotificationDelivery>();
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => delivery.ExecuteAsync(
            keys.Message(NotificationTestKeys.Content with { Subject = "subject\r\nBcc: other@example.com" }), TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => delivery.ExecuteAsync(
            keys.Message(changeMetadata: metadata => metadata with { Version = 2 }), TestContext.Current.CancellationToken));
        var expired = keys.Message(changeMetadata: metadata => metadata with { OccurredAt = DateTimeOffset.UtcNow.AddHours(-1), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
        Assert.Equal(NotificationDeliveryResult.Expired, await delivery.ExecuteAsync(expired, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(keys.EmailsPath));
    }

    [Fact]
    public async Task Missing_provider_fails_and_legacy_or_unknown_configuration_is_rejected()
    {
        using var keys = new NotificationTestKeys();
        using (var host = Host(keys, new() { ["Notifications:Email:Provider"] = "None" }))
        {
            using var scope = host.Services.CreateScope();
            var transport = scope.ServiceProvider.GetRequiredService<IEmailTransport>();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => transport.SendAsync(Guid.NewGuid(), NotificationTestKeys.Content, TestContext.Current.CancellationToken));
            Assert.Contains("Notifications:Email:Provider", error.Message);
        }
        foreach (var changes in new Dictionary<string, string?>[]
        {
            new() { ["Notifications:Email:Provider"] = "NotAProvider" },
            new() { ["Identity:Email:Provider"] = "PickupDirectory" },
        })
        {
            using var host = Host(keys, changes);
            Assert.Throws<OptionsValidationException>(() => host.Services.GetRequiredService<IOptions<NotificationOptions>>().Value);
        }
    }

    [Fact]
    public async Task Separate_worker_process_consumes_protected_contract_without_identity_or_sensitive_logs()
    {
        using var keys = new NotificationTestKeys();
        var message = keys.Message();
        await PurgeAsync();
        await NotificationDatabaseScenario.ResetAsync(database);
        using var process = StartWorker(keys);
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            await PublishAsync(EnvelopeCodec.Encode(message));
            await WaitAsync(() => Task.FromResult(Directory.Exists(keys.EmailsPath) && Directory.GetFiles(keys.EmailsPath, "*.eml").Length == 1));
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); }
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        }
        var logs = await output + await error;
        Assert.DoesNotContain(NotificationTestKeys.Content.Recipient, logs);
        Assert.DoesNotContain("private-test-token", logs);
        Assert.DoesNotContain(message.Payload.Ciphertext, logs);
        Assert.DoesNotContain("PRIVATE KEY", logs);
        var refs = typeof(INotificationDelivery).Assembly.GetReferencedAssemblies().Select(assembly => assembly.Name!);
        Assert.DoesNotContain(refs, name => name.Contains("Identity", StringComparison.Ordinal) || name.Contains("Settings", StringComparison.Ordinal));
        await PurgeAsync();
    }

    [Fact]
    public async Task Worker_process_logs_json_lines_that_continue_each_message_trace_without_deduplicating_by_trace()
    {
        const string TraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
        using var keys = new NotificationTestKeys();
        var traced = new[] { "opaque-trace-a", "opaque-trace-b" }.Select(key => keys.Message(changeMetadata: metadata => metadata with
        { SupersessionKey = key, Traceparent = $"00-{TraceId}-00f067aa0ba902b7-01", Tracestate = "vendor=opaque" })).ToArray();
        var untraced = keys.Message(changeMetadata: metadata => metadata with { SupersessionKey = "opaque-trace-c", Traceparent = null, Tracestate = null });
        await PurgeAsync();
        await NotificationDatabaseScenario.ResetAsync(database);
        using var process = StartWorker(keys);
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            foreach (var message in traced.Append(untraced)) { await PublishAsync(EnvelopeCodec.Encode(message), message.MessageId); }
            await WaitAsync(() => Task.FromResult(Directory.Exists(keys.EmailsPath) && Directory.GetFiles(keys.EmailsPath, "*.eml").Length == 3));
            // O console exporta em lotes de até 1 s; o processo é encerrado à força depois disso.
            await Task.Delay(2500, TestContext.Current.CancellationToken);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); }
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        }
        var diagnosticError = await error;
        Assert.True(diagnosticError.Length == 0, diagnosticError);
        var lines = (await output).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var entries = lines.Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        Assert.All(entries, entry => Assert.Equal("notifications", entry.GetProperty("service").GetString()));
        Assert.All(entries, entry => Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("serviceVersion").GetString())));
        string[] TracesOf(Guid id) => entries.Where(entry => entry.TryGetProperty("attributes", out var attributes)
                && attributes.TryGetProperty("MessageId", out var value) && value.GetString() == id.ToString("D"))
            .Select(entry => entry.GetProperty("traceId").GetString()!).ToArray();
        foreach (var message in traced)
        {
            var traces = TracesOf(message.MessageId);
            Assert.True(traces.Length >= 2);
            Assert.All(traces, trace => Assert.Equal(TraceId, trace));
        }
        var fresh = TracesOf(untraced.MessageId);
        Assert.True(fresh.Length >= 2);
        Assert.All(fresh, trace => Assert.Matches("^[0-9a-f]{32}$", trace));
        Assert.All(fresh, trace => Assert.NotEqual(TraceId, trace));
        Assert.DoesNotContain(NotificationTestKeys.Content.Recipient, await output);
    }

    [Fact]
    public async Task Incompatible_message_is_durably_classified_and_acked_without_copying_original_to_failure_queue()
    {
        using var keys = new NotificationTestKeys();
        var message = keys.Message(changeMetadata: metadata => metadata with { Version = 2 });
        await PurgeAsync();
        await NotificationDatabaseScenario.ResetAsync(database);
        using var process = StartWorker(keys);
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            await PublishAsync(EnvelopeCodec.Encode(message));
            await WaitAsync(async () =>
            {
                await using var context = NotificationDatabaseScenario.Context(database);
                return await context.Failures.AnyAsync(row => row.Code == "unsupported-version", TestContext.Current.CancellationToken);
            });
            await using var connection = await broker.ConnectAsync();
            await using var channel = await connection.CreateChannelAsync(cancellationToken: TestContext.Current.CancellationToken);
            await WaitAsync(async () => (await channel.QueueDeclarePassiveAsync(RabbitMqFixture.FailuresQueue, TestContext.Current.CancellationToken)).MessageCount > 0);
            var reference = await channel.BasicGetAsync(RabbitMqFixture.FailuresQueue, true, TestContext.Current.CancellationToken);
            Assert.NotNull(reference);
            var text = System.Text.Encoding.UTF8.GetString(reference.Body.Span);
            Assert.Contains("unsupported-version", text);
            Assert.DoesNotContain(message.Payload.Ciphertext, text);
            Assert.DoesNotContain(NotificationTestKeys.Content.Recipient, text);
            Assert.Null(await channel.BasicGetAsync(RabbitMqFixture.Queue, true, TestContext.Current.CancellationToken));
            Assert.False(process.HasExited);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); }
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        }
        Assert.DoesNotContain("private-test-token", await output + await error);
        Assert.False(Directory.Exists(keys.EmailsPath));
    }

    private static IHost Host(NotificationTestKeys keys, Dictionary<string, string?>? changes = null)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Development,
            ContentRootPath = keys.DirectoryPath,
            ApplicationName = typeof(NotificationsWorkerTests).Assembly.GetName().Name,
        });
        var settings = new Dictionary<string, string?>
        {
            ["Notifications:Consumer:Enabled"] = "false",
            ["Notifications:Email:Provider"] = "PickupDirectory",
            ["Notifications:Email:PickupDirectory"] = keys.EmailsPath,
            ["Notifications:PrivateKeyRingPath"] = keys.RingPath,
        };
        foreach (var pair in changes ?? []) { settings[pair.Key] = pair.Value; }
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddNotificationsService();
        return builder.Build();
    }

    private Process StartWorker(NotificationTestKeys keys, IReadOnlyDictionary<string, string>? overrides = null)
    {
        var configuration = typeof(NotificationsWorkerTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        var path = RepositoryRoot.Resolve("src", "Services", "Notifications", "Ouroboros.Notifications.Worker", "bin", configuration, "net10.0", "Ouroboros.Notifications.Worker.dll");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(path);
        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("Identity__", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Notifications__", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ConnectionStrings__", StringComparison.OrdinalIgnoreCase)).ToArray())
        { start.Environment.Remove(name); }
        start.Environment["DOTNET_ENVIRONMENT"] = "Development";
        start.Environment["Notifications__Health__Urls__0"] = "http://127.0.0.1:0";
        start.Environment["Notifications__Consumer__Enabled"] = "true";
        start.Environment["Notifications__Email__Provider"] = "PickupDirectory";
        start.Environment["Notifications__Email__PickupDirectory"] = keys.EmailsPath;
        start.Environment["Notifications__PrivateKeyRingPath"] = keys.RingPath;
        start.Environment["Notifications__FingerprintKeyRingPath"] = keys.FingerprintRingPath;
        start.Environment["ConnectionStrings__Notifications"] = database.ConnectionStringFor(ProvisionedInstance.NotificationsRuntime, ProvisionedInstance.NotificationsDatabase);
        start.Environment["Notifications__Broker__Host"] = broker.Host;
        start.Environment["Notifications__Broker__Port"] = broker.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["Notifications__Broker__Password"] = broker.ConsumerPassword;
        start.Environment["Notifications__FailuresBroker__Host"] = broker.Host;
        start.Environment["Notifications__FailuresBroker__Port"] = broker.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["Notifications__FailuresBroker__Password"] = broker.FailuresPassword;
        foreach (var pair in overrides ?? new Dictionary<string, string>()) { start.Environment[pair.Key] = pair.Value; }
        return Process.Start(start) ?? throw new InvalidOperationException("Worker não iniciou.");
    }

    private async Task PublishAsync(byte[] body, Guid? messageId = null)
    {
        await using var connection = await broker.ConnectAsync("identity_publisher", broker.IdentityPassword);
        await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(true, true), TestContext.Current.CancellationToken);
        await channel.BasicPublishAsync(RabbitMqFixture.Exchange, "email.requested.v1", true, new BasicProperties { Persistent = true, MessageId = messageId?.ToString("D") }, body, TestContext.Current.CancellationToken);
    }

    private async Task PurgeAsync()
    {
        await using var connection = await broker.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync(cancellationToken: TestContext.Current.CancellationToken);
        await WaitAsync(async () => (await channel.QueueDeclarePassiveAsync(RabbitMqFixture.Queue, TestContext.Current.CancellationToken)).ConsumerCount == 0);
        await channel.QueuePurgeAsync(RabbitMqFixture.Queue, cancellationToken: TestContext.Current.CancellationToken);
        await channel.QueuePurgeAsync(RabbitMqFixture.FailuresQueue, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static async Task WaitAsync(Func<Task<bool>> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (!await condition()) { await Task.Delay(100, timeout.Token); }
    }
}
