using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ouroboros.Identity.Infrastructure.Messaging;
using Ouroboros.Identity.Infrastructure.Persistence;
using Ouroboros.Notifications.Domain;

namespace Ouroboros.IntegrationTests;

public sealed partial class NotificationsWorkerTests
{
    [Fact]
    public async Task Api_client_notices_cross_real_broker_and_worker_without_exposing_secret_or_reapplying_registration()
    {
        await PurgeAsync();
        await using var notifications = await NotificationDatabaseScenario.CreateAsync(database);
        await Ouroboros.Identity.DependencyInjection.MigrateDatabaseAsync(
            database.ConnectionStringFor(ProvisionedInstance.IdentityMigrator, ProvisionedInstance.IdentityDatabase), Cancellation);
        Assert.Equal(0, (await database.ProvisionAsync()).ExitCode);
        await using var api = new BackofficeIntegrationApiFactory(database.ConnectionStringFor(ProvisionedInstance.IdentityRuntime, ProvisionedInstance.IdentityDatabase));
        await ApiClientTestSupport.PrepareAsync(api.Services);
        Guid[] ownUsers;
        Guid[] previousIntents;
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            ownUsers = await db.Users.Where(user => user.Email == "admin@adnerscarpelini.com" || user.Email == "oi@adnerscarpelini.com" || user.Email == "inactive@adnerscarpelini.com")
                .Select(user => user.ExternalId).ToArrayAsync(Cancellation);
            previousIntents = await db.EmailIntents.Select(intent => intent.ExternalId).ToArrayAsync(Cancellation);
        }
        using var client = await AdminInvitationTests.AdminAsync(api);
        using var created = await ApiClientRegistrationTests.RegisterAsync(client);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var payload = await created.Content.ReadFromJsonAsync<JsonElement>(Cancellation);
        var clientId = payload.GetProperty("client_id").GetGuid();
        var secret = payload.GetProperty("client_secret").GetString()!;
        var hash = Convert.ToHexString(Ouroboros.Identity.Application.Security.OpaqueTokens.Hash(secret));
        Guid[] ownIntents;
        await using (var scope = api.Services.CreateAsyncScope())
        {
            ownIntents = await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().EmailIntents.Where(intent => !previousIntents.Contains(intent.ExternalId))
                .Select(intent => intent.ExternalId).ToArrayAsync(Cancellation);
        }
        Assert.Equal(2, ownIntents.Length);
        using var metrics = new EmailOutboxMetrics();
        using var publisher = new EmailOutboxPublisher(api.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new EmailPublisherOptions { Host = broker.Host, Port = broker.Port, Password = broker.IdentityPassword }),
            notifications.Services.GetRequiredService<IHostEnvironment>(), TimeProvider.System, metrics, NullLogger<EmailOutboxPublisher>.Instance);
        await publisher.RunOnceAsync(Cancellation);
        Assert.False(Directory.Exists(notifications.Keys.EmailsPath));
        using var worker = StartWorker(notifications.Keys, new Dictionary<string, string> { ["Notifications__PrivateKeyRingPath"] = TestSigningKeys.EmailPrivateKeyRingPath });
        var output = worker.StandardOutput.ReadToEndAsync(Cancellation);
        var error = worker.StandardError.ReadToEndAsync(Cancellation);
        try
        {
            await WaitAsync(async () =>
            {
                await using var inbox = notifications.Context();
                return await inbox.Inbox.CountAsync(row => row.State == InboxState.Delivered, Cancellation) == 2;
            });
            await publisher.RunOnceAsync(Cancellation);
            Assert.Equal(2, Directory.GetFiles(notifications.Keys.EmailsPath, "*.eml").Length);
            foreach (var id in ownIntents)
            {
                var body = await File.ReadAllTextAsync(Path.Combine(notifications.Keys.EmailsPath, id.ToString("N") + ".eml"), Cancellation);
                Assert.Contains(clientId.ToString(), body);
                Assert.DoesNotContain(secret, body);
                Assert.DoesNotContain(hash, body, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("inactive@adnerscarpelini.com", body);
            }
            await using var scope = api.Services.CreateAsyncScope();
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().ApiClients.CountAsync(row => row.ExternalId == clientId, Cancellation));
        }
        finally
        {
            await StopAsync(worker);
            await using var scope = api.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            await db.EmailIntents.Where(intent => ownIntents.Contains(intent.ExternalId)).ExecuteDeleteAsync(Cancellation);
            await db.ApiClients.Where(row => row.ExternalId == clientId).ExecuteDeleteAsync(Cancellation);
            var users = db.Users.Where(user => ownUsers.Contains(user.ExternalId));
            var sessionIds = db.Sessions.Where(session => users.Select(user => user.Id).Contains(session.UserId)).Select(session => session.Id);
            await db.RefreshTokens.Where(token => sessionIds.Contains(token.SessionId)).ExecuteDeleteAsync(Cancellation);
            await db.Sessions.Where(session => sessionIds.Contains(session.Id)).ExecuteDeleteAsync(Cancellation);
            await users.ExecuteDeleteAsync(Cancellation);
        }
        var logs = await output + await error;
        Assert.DoesNotContain(secret, logs);
        Assert.DoesNotContain(hash, logs, StringComparison.OrdinalIgnoreCase);
    }
}
