using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace Ouroboros.IntegrationTests;

// Cada módulo é dono do seu schema: o modelo de cada DbContext só tem tabelas no schema do próprio
// módulo, então nenhum módulo mapeia (e lê) tabelas de outro.
public sealed class ModuleDataBoundaryTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    [Fact]
    public void Each_module_db_context_maps_only_its_own_schema()
    {
        using var scope = Api.Services.CreateScope();

        foreach (var context in ModuleDbContexts.Resolve(scope.ServiceProvider))
        {
            var model = context.GetService<IDesignTimeModel>().Model;
            var schema = model.GetDefaultSchema();
            Assert.False(string.IsNullOrEmpty(schema), $"{context.GetType().Name} não define schema padrão.");
            Assert.All(
                model.GetRelationalModel().Tables,
                table => Assert.True(table.Schema == schema, $"{context.GetType().Name}: tabela {table.Schema}.{table.Name} fora de {schema}."));
        }
    }

    [Fact]
    public void Identity_context_is_discovered()
    {
        using var scope = Api.Services.CreateScope();

        var names = ModuleDbContexts.Resolve(scope.ServiceProvider).Select(context => context.GetType().Name);

        Assert.Equal(["IdentityDbContext"], names);
    }
}
