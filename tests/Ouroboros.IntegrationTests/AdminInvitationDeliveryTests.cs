using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ouroboros.Identity.Application.Security;
using Ouroboros.Identity.Domain.Notifications;
using Ouroboros.Identity.Domain.Users;
using Ouroboros.Identity.Infrastructure.Messaging;
using Ouroboros.Identity.Infrastructure.Persistence;
using Ouroboros.Messaging;
using Ouroboros.Notifications.Domain;
using Ouroboros.Notifications.Infrastructure;

namespace Ouroboros.IntegrationTests;

public sealed partial class NotificationsWorkerTests
{
    [Fact]
    public async Task Admin_invitation_survives_broker_outage_and_worker_delivers_link_and_token_free_notices()
    {
        await PurgeAsync();
        await using var notifications = await NotificationDatabaseScenario.CreateAsync(database);
        await Ouroboros.Identity.DependencyInjection.MigrateDatabaseAsync(
            database.ConnectionStringFor(ProvisionedInstance.IdentityMigrator, ProvisionedInstance.IdentityDatabase), Cancellation);
        Assert.Equal(0, (await database.ProvisionAsync()).ExitCode);
        await using var api = new BackofficeIntegrationApiFactory(database.ConnectionStringFor(ProvisionedInstance.IdentityRuntime, ProvisionedInstance.IdentityDatabase));
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            db.Users.Add(User.Create("admin@adnerscarpelini.com", scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(AdminInvitationTests.Password), UserRole.Admin));
            await db.SaveChangesAsync(Cancellation);
        }
        using var client = await AdminInvitationTests.AdminAsync(api);
        using var metrics = new EmailOutboxMetrics();
        using var publisher = new EmailOutboxPublisher(api.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new EmailPublisherOptions { Host = broker.Host, Port = broker.Port, Password = broker.IdentityPassword }),
            notifications.Services.GetRequiredService<IHostEnvironment>(), TimeProvider.System, metrics, NullLogger<EmailOutboxPublisher>.Instance);
        Guid linkId;
        string rawToken;
        var createdIntentIds = new List<Guid>();
        await broker.StopAsync();
        try
        {
            using var invitation = await AdminInvitationTests.InviteAsync(client);
            Assert.Equal(HttpStatusCode.Accepted, invitation.StatusCode);
            Assert.DoesNotContain("token", await invitation.Content.ReadAsStringAsync(Cancellation), StringComparison.OrdinalIgnoreCase);
            rawToken = await TestOutbox.TokenAsync(api.Services, AdminInvitationTests.Recipient);
            await publisher.RunOnceAsync(Cancellation);
            await using var scope = api.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            var intents = await db.EmailIntents.AsNoTracking().Where(intent => intent.State == EmailIntentState.Pending).ToArrayAsync(Cancellation);
            Assert.Equal(2, intents.Length);
            createdIntentIds.AddRange(intents.Select(intent => intent.ExternalId));
            Assert.All(intents, intent => { Assert.Equal(EmailIntentState.Pending, intent.State); Assert.NotNull(intent.ProtectedEnvelope); });
            var reader = new ProtectedEmailPayloadReader(Options.Create(new NotificationOptions { PrivateKeyRingPath = TestSigningKeys.EmailPrivateKeyRingPath }));
            linkId = Assert.Single(intents, intent =>
            {
                Assert.True(EnvelopeCodec.TryDecode(intent.ProtectedEnvelope!, "notification.email.requested", 1, "identity", out var envelope, out _));
                return reader.Read(envelope!).Recipient == AdminInvitationTests.Recipient;
            }).ExternalId;
            Assert.Single(await db.AdminInvitations.ToArrayAsync(Cancellation));
        }
        finally { await broker.StartAsync(); }
        using var worker = StartWorker(notifications.Keys, new Dictionary<string, string> { ["Notifications__PrivateKeyRingPath"] = TestSigningKeys.EmailPrivateKeyRingPath });
        var output = worker.StandardOutput.ReadToEndAsync(Cancellation);
        var error = worker.StandardError.ReadToEndAsync(Cancellation);
        try
        {
            await using (var scope = api.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().EmailIntents.ExecuteUpdateAsync(
                    updates => updates.SetProperty(intent => intent.NextAttemptAt, (DateTimeOffset?)null), Cancellation);
            }
            await publisher.RunOnceAsync(Cancellation);
            var token = await DeliveredTokenAsync(notifications, linkId);
            Assert.Equal(rawToken, token);
            using var complete = await client.PostAsJsonAsync("/api/v1/admin/identity/admin-invitations/complete",
                new { Token = token, Password = AdminInvitationTests.Password }, Cancellation);
            Assert.Equal(HttpStatusCode.NoContent, complete.StatusCode);
            await using (var scope = api.Services.CreateAsyncScope())
            {
                createdIntentIds.AddRange(await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().EmailIntents
                    .Where(intent => intent.State == EmailIntentState.Pending).Select(intent => intent.ExternalId).ToArrayAsync(Cancellation));
            }
            await publisher.RunOnceAsync(Cancellation);
            await WaitAsync(async () =>
            {
                await using var inbox = notifications.Context();
                return await inbox.Inbox.CountAsync(row => row.State == InboxState.Delivered, Cancellation) == 4;
            });
            var paths = Directory.GetFiles(notifications.Keys.EmailsPath, "*.eml");
            Assert.Equal(4, paths.Length);
            foreach (var path in paths.Where(path => Path.GetFileName(path) != linkId.ToString("N") + ".eml"))
            { Assert.DoesNotContain(rawToken, await File.ReadAllTextAsync(path, Cancellation)); }
        }
        finally
        {
            await StopAsync(worker);
            await using var scope = api.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            // A fixture da classe é compartilhada: remove apenas os artefatos deste fluxo.
            var invitationId = await db.AdminInvitations.Where(invitation => invitation.Email == AdminInvitationTests.Recipient)
                .Select(invitation => invitation.ExternalId).SingleAsync(Cancellation);
            await db.EmailIntents.Where(intent => createdIntentIds.Contains(intent.ExternalId)).ExecuteDeleteAsync(Cancellation);
            await db.AdminInvitations.Where(invitation => invitation.ExternalId == invitationId).ExecuteDeleteAsync(Cancellation);
            var users = db.Users.Where(user => user.Email == "admin@adnerscarpelini.com" || user.Email == AdminInvitationTests.Recipient);
            var sessionIds = db.Sessions.Where(session => users.Select(user => user.Id).Contains(session.UserId)).Select(session => session.Id);
            await db.RefreshTokens.Where(token => sessionIds.Contains(token.SessionId)).ExecuteDeleteAsync(Cancellation);
            await db.Sessions.Where(session => sessionIds.Contains(session.Id)).ExecuteDeleteAsync(Cancellation);
            await users.ExecuteDeleteAsync(Cancellation);
        }
        var logs = await output + await error;
        Assert.DoesNotContain(rawToken, logs);
        Assert.DoesNotContain(AdminInvitationTests.Recipient, logs);
        Assert.DoesNotContain("?token=", logs);
    }
}
