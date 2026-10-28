using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ouroboros.Identity.Infrastructure.Persistence;

namespace Ouroboros.IntegrationTests;

// A organização de pastas, os nomes de projeto e os namespaces de Identity não alteram o modelo de dados:
// schema, histórico e ids das migrations ficam como estão em bancos existentes, e mover código não gera migration.
public sealed class IdentityModelStabilityTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private static readonly string[] ExpectedMigrations =
    [
        "20261022195724_InitialCreate",
        "20261022211600_AddUsers",
        "20261023043958_AddSessions",
        "20261023045710_AddAccountLockout",
        "20261023052955_AddRefreshTokenRevocation",
        "20261023094930_AddEmailTokens",
        "20261025142848_OwnIdentityParameters",
        "20261026015350_AddEmailIntents",
        "20261026025858_AddEmailPublicationLeases",
        "20261028002017_AddAdminInvitations",
        "20261028082611_AddApiClients",
        "20261028091043_AddApiClientDeactivationAudit",
    ];

    [Fact]
    public void Migration_ids_schema_and_history_table_are_unchanged_and_the_model_has_no_pending_changes()
    {
        using var scope = Api.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();

        Assert.Equal(ExpectedMigrations, context.Database.GetMigrations());
        Assert.Equal("identity", IdentityDbContext.Schema);
        Assert.Equal("identity", context.Model.GetDefaultSchema());
        Assert.False(context.Database.HasPendingModelChanges());
    }
}
