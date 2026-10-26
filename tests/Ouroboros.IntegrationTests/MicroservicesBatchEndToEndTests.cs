using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Ouroboros.Identity.Domain.Notifications;
using Ouroboros.Identity.Infrastructure.Messaging;
using Ouroboros.Identity.Infrastructure.Persistence;
using Ouroboros.Notifications.Domain;

namespace Ouroboros.IntegrationTests;

public sealed partial class NotificationsWorkerTests
{
    [Fact]
    public async Task Deferred_publication_and_worker_export_same_trace_and_bounded_metric_labels_to_real_collector()
    {
        const string trace = "4bf92f3577b34da6a3ce929d0e0e4736";
        await using var pipeline = new DiagnosticsPipelineFixture();
        await pipeline.InitializeAsync();
        await PurgeAsync();
        await using var notifications = await NotificationDatabaseScenario.CreateAsync(database);
        await using var factory = new IntegrationApiFactory(database.ConnectionStringFor(ProvisionedInstance.IdentityMigrator, ProvisionedInstance.IdentityDatabase));
        await using var api = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(configuration =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Observability:Otlp:Endpoint"] = pipeline.OtlpEndpoint })));
        using var client = api.CreateClient();
        await using (var scope = api.Services.CreateAsyncScope())
        { await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Database.MigrateAsync(Cancellation); }
        using var worker = StartWorker(notifications.Keys, new Dictionary<string, string>
        {
            ["Notifications__PrivateKeyRingPath"] = TestSigningKeys.EmailPrivateKeyRingPath,
            ["Observability__Otlp__Endpoint"] = pipeline.OtlpEndpoint,
        });
        var output = worker.StandardOutput.ReadToEndAsync(Cancellation);
        var error = worker.StandardError.ReadToEndAsync(Cancellation);
        const string email = "distributed-trace@example.com";
        Guid id = Guid.Empty;
        try
        {
            using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/identity/registrations?token=never-export-this-query"))
            {
                request.Headers.Add("traceparent", $"00-{trace}-00f067aa0ba902b7-00");
                request.Content = JsonContent.Create(new { Email = email });
                using var response = await client.SendAsync(request, Cancellation);
                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            }
            await using (var scope = api.Services.CreateAsyncScope())
            {
                id = await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().EmailIntents.Where(row => row.State == EmailIntentState.Pending)
                    .Select(row => row.ExternalId).SingleAsync(Cancellation);
            }
            Assert.False(File.Exists(Path.Combine(notifications.Keys.EmailsPath, id.ToString("N") + ".eml")));
            using var metrics = new EmailOutboxMetrics();
            using var publisher = new EmailOutboxPublisher(api.Services.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new EmailPublisherOptions { Host = broker.Host, Port = broker.Port, Password = broker.IdentityPassword }),
                notifications.Services.GetRequiredService<IHostEnvironment>(), TimeProvider.System, metrics, NullLogger<EmailOutboxPublisher>.Instance);
            await publisher.RunOnceAsync(Cancellation);
            var deliveredToken = await DeliveredTokenAsync(notifications, id);
            await WaitAsync(async () => (await pipeline.StoredTextAsync("traces")).Contains("inbox.deliver", StringComparison.Ordinal));
            var spans = (await pipeline.StoredTextAsync("traces")).Split('\n', StringSplitOptions.RemoveEmptyEntries).SelectMany(line =>
            {
                using var json = JsonDocument.Parse(line);
                return json.RootElement.GetProperty("resourceSpans").EnumerateArray().SelectMany(resource =>
                    resource.GetProperty("scopeSpans").EnumerateArray().SelectMany(scope => scope.GetProperty("spans").EnumerateArray().Select(span => span.Clone()))).ToArray();
            }).ToArray();
            foreach (var name in new[] { "outbox.publish", "inbox.accept", "inbox.deliver" })
            {
                var span = Assert.Single(spans, span => span.GetProperty("name").GetString() == name);
                Assert.Equal(trace, span.GetProperty("traceId").GetString());
                Assert.Single(span.GetProperty("links").EnumerateArray());
                Assert.Equal(id.ToString("D"), OtlpJson.Attribute(span, "messaging.message.id"));
            }
            Assert.Contains(spans, span => span.GetProperty("traceId").GetString() == trace
                && OtlpJson.Attribute(span, "http.request.method") == "POST");
            var cycle = Assert.Single(spans, span => span.GetProperty("name").GetString() == "outbox.publish.cycle");
            Assert.NotEqual(trace, cycle.GetProperty("traceId").GetString());
            Assert.Single(cycle.GetProperty("links").EnumerateArray());
            await WaitAsync(async () => (await pipeline.StoredTextAsync("metrics")).Contains("inbox.delivery.attempts", StringComparison.Ordinal));
            var metricText = await pipeline.StoredTextAsync("metrics");
            foreach (var line in metricText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                using var json = JsonDocument.Parse(line);
                foreach (var resource in json.RootElement.GetProperty("resourceMetrics").EnumerateArray())
                    foreach (var scope in resource.GetProperty("scopeMetrics").EnumerateArray())
                        foreach (var metric in scope.GetProperty("metrics").EnumerateArray())
                            foreach (var data in metric.EnumerateObject().Where(property => property.Value.ValueKind == JsonValueKind.Object && property.Value.TryGetProperty("dataPoints", out _)))
                                foreach (var point in data.Value.GetProperty("dataPoints").EnumerateArray())
                                {
                                    if (!point.TryGetProperty("attributes", out var attributes)) { continue; }
                                    Assert.All(attributes.EnumerateArray(), attribute => Assert.Contains(attribute.GetProperty("key").GetString(),
                                        new[] { "code", "state", "result", "outcome", "http.route", "http.request.method", "http.response.status_code" }));
                                }
            }
            var diagnostics = await pipeline.StoredTextAsync() + await pipeline.StoredTextAsync("traces") + metricText;
            foreach (var secret in new[] { email, deliveredToken, "never-export-this-query", "?token=", "PRIVATE KEY" })
            { Assert.DoesNotContain(secret, diagnostics); }
        }
        finally
        {
            await StopAsync(worker);
            // A fixture compartilha o banco entre métodos; remova só os dados fictícios deste cenário.
            await using var scope = api.Services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            await context.EmailIntents.Where(row => row.ExternalId == id).ExecuteDeleteAsync(Cancellation);
            await context.EmailTokens.Where(row => row.Email == email).ExecuteDeleteAsync(Cancellation);
        }
        Assert.DoesNotContain(email, await output + await error);
    }

    [Fact]
    public async Task Registration_and_password_reset_cross_real_broker_and_separate_worker_using_isolated_databases_then_issue_valid_rsa_tokens()
    {
        await PurgeAsync();
        await using var notifications = await NotificationDatabaseScenario.CreateAsync(database);
        await using (var migrationApi = new IntegrationApiFactory(database.ConnectionStringFor(ProvisionedInstance.IdentityMigrator, ProvisionedInstance.IdentityDatabase)))
        {
            await using var migrationScope = migrationApi.Services.CreateAsyncScope();
            await migrationScope.ServiceProvider.GetRequiredService<IdentityDbContext>().Database.MigrateAsync(Cancellation);
        }
        Assert.True((await database.ProvisionAsync()).ExitCode == 0);
        await using var api = new IntegrationApiFactory(database.ConnectionStringFor(ProvisionedInstance.IdentityRuntime, ProvisionedInstance.IdentityDatabase));
        using var client = api.CreateClient();
        const string email = "whole-batch@example.com";
        const string password = "uma frase longa e secreta do cliente recente";
        const string changedPassword = "outra frase longa e secreta para validar o lote";
        using var worker = StartWorker(notifications.Keys, new Dictionary<string, string>
        { ["Notifications__PrivateKeyRingPath"] = TestSigningKeys.EmailPrivateKeyRingPath });
        var workerOutput = worker.StandardOutput.ReadToEndAsync(Cancellation);
        var workerError = worker.StandardError.ReadToEndAsync(Cancellation);
        using var metrics = new EmailOutboxMetrics();
        using var publisher = new EmailOutboxPublisher(api.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new EmailPublisherOptions { Host = broker.Host, Port = broker.Port, Password = broker.IdentityPassword }),
            notifications.Services.GetRequiredService<IHostEnvironment>(), TimeProvider.System, metrics, NullLogger<EmailOutboxPublisher>.Instance);
        try
        {
            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/v1/identity/registrations", new { Email = email }, Cancellation)).StatusCode);
            var registrationId = await PendingIdAsync(api);
            Assert.False(File.Exists(Path.Combine(notifications.Keys.EmailsPath, registrationId.ToString("N") + ".eml")));
            await publisher.RunOnceAsync(Cancellation);
            var registrationToken = await DeliveredTokenAsync(notifications, registrationId);
            Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/identity/registrations/complete",
                new { Token = registrationToken, Password = password }, Cancellation)).StatusCode);
            using var login = await client.PostAsJsonAsync("/api/v1/identity/sessions", new { Email = email, Password = password }, Cancellation);
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            using var session = JsonDocument.Parse(await login.Content.ReadAsStringAsync(Cancellation));
            var access = session.RootElement.GetProperty("accessToken").GetString()!;
            var token = new JsonWebToken(access);
            Assert.Equal("RS256", token.Alg);
            Assert.Equal("test-key", token.Kid);
            Assert.Equal("user", token.GetClaim("principal_type").Value);
            using var me = new HttpRequestMessage(HttpMethod.Get, "/api/v1/identity/me");
            me.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(me, Cancellation)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/identity/keys", Cancellation)).StatusCode);
            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/v1/identity/password-resets", new { Email = email }, Cancellation)).StatusCode);
            var resetId = await PendingIdAsync(api);
            Assert.NotEqual(registrationId, resetId);
            await publisher.RunOnceAsync(Cancellation);
            var resetToken = await DeliveredTokenAsync(notifications, resetId);
            Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/identity/password-resets/complete",
                new { Token = resetToken, Password = changedPassword }, Cancellation)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/identity/sessions", new { Email = email, Password = password }, Cancellation)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/identity/sessions", new { Email = email, Password = changedPassword }, Cancellation)).StatusCode);
            await WaitForEmptyMainQueueAsync();
            await using var scope = api.Services.CreateAsyncScope();
            var source = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            Assert.Equal(2, await source.EmailIntents.CountAsync(row => row.State == EmailIntentState.Published && row.ProtectedEnvelope == null, Cancellation));
            await using var inbox = notifications.Context();
            Assert.Equal(2, await inbox.Inbox.CountAsync(row => row.State == InboxState.Delivered && row.Attempts == 1 && row.ProtectedEnvelope == null, Cancellation));
            Assert.Equal(2, Directory.GetFiles(notifications.Keys.EmailsPath, "*.eml").Length);
        }
        finally { await StopAsync(worker); }
        var logs = await workerOutput + await workerError;
        Assert.DoesNotContain(email, logs);
        Assert.DoesNotContain("?token=", logs);
        Assert.DoesNotContain("PRIVATE KEY", logs);
    }

    private static async Task<Guid> PendingIdAsync(IntegrationApiFactory api)
    {
        await using var scope = api.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().EmailIntents.Where(row => row.State == EmailIntentState.Pending)
            .Select(row => row.ExternalId).SingleAsync(Cancellation);
    }

    private static async Task<string> DeliveredTokenAsync(NotificationDatabaseScenario scenario, Guid id)
    {
        var path = Path.Combine(scenario.Keys.EmailsPath, id.ToString("N") + ".eml");
        await WaitAsync(async () =>
        {
            await using var context = scenario.Context();
            return File.Exists(path) && await context.Inbox.AnyAsync(row => row.MessageId == id && row.State == InboxState.Delivered, Cancellation);
        });
        var text = await File.ReadAllTextAsync(path, Cancellation);
        var match = TokenFromDeliveredEmail().Match(text);
        Assert.True(match.Success, "E-mail entregue não contém o contrato de link esperado.");
        return Uri.UnescapeDataString(match.Groups[1].Value);
    }

    [GeneratedRegex(@"[?&]token=([^\s&]+)")]
    private static partial Regex TokenFromDeliveredEmail();
}
