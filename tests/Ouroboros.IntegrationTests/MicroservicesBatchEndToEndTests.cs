using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
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
