using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ouroboros.Identity.Application.Notifications;
using Ouroboros.Identity.Infrastructure.Persistence;

namespace Ouroboros.IntegrationTests;

public sealed partial class IdentityDatabaseTransferTests
{
    [Fact]
    public async Task Rollback_refuses_target_only_outbox_migration_instead_of_reporting_no_new_writes()
    {
        await using var scenario = await ScenarioAsync();
        // Reproduz a origem anterior à #85 apenas nos bancos desta fixture.
        await Instance.ExecuteAdminAsync(scenario.Source, """
            DROP TABLE identity.email_intents;
            DELETE FROM identity."__EFMigrationsHistory" WHERE "MigrationId" >= '20261026015350_AddEmailIntents';
            """);
        var transfer = await scenario.CutOverAsync();
        await using (var target = new IntegrationApiFactory(Instance.ConnectionStringFor(
            ProvisionedInstance.IdentityMigrator, ProvisionedInstance.IdentityDatabase)))
        {
            await using var scope = target.Services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            await context.Database.MigrateAsync(Cancellation);
            var token = await context.EmailTokens.AsNoTracking().FirstAsync(Cancellation);
            var intent = scope.ServiceProvider.GetRequiredService<IEmailIntentFactory>().Create(
                new EmailMessage(token.Email, "Recovered intention", "protected-recovery-content"), "registration", DateTimeOffset.UtcNow, token.ExpiresAt);
            intent.BindToToken(token.ExternalId);
            context.EmailIntents.Add(intent);
            await context.SaveChangesAsync(Cancellation);
            Assert.Equal(1, await context.EmailIntents.CountAsync(Cancellation));
        }
        NpgsqlConnection.ClearAllPools();
        var rollback = await Instance.TransferAsync(scenario.Source, "rollback", transfer.Id);
        var reconcile = await Instance.TransferAsync(scenario.Source, "reconcile", transfer.Id);
        Assert.NotEqual(0, reconcile.ExitCode);
        Assert.Contains("migrations", reconcile.Stderr, StringComparison.Ordinal);
        Assert.NotEqual(0, rollback.ExitCode);
    }
}
