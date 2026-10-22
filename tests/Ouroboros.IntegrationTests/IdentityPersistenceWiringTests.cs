using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Ouroboros.Modules.Identity.Infrastructure.Persistence;

namespace Ouroboros.IntegrationTests;

// Garante que o IdentityDbContext real, resolvido pela DI da API, usa o interceptor de datas e
// mantém a tabela de histórico de migrations com as colunas originais.
public sealed class IdentityPersistenceWiringTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    [Fact]
    public void Identity_db_context_uses_audit_interceptor()
    {
        using var scope = Api.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();

        var interceptors = context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.Interceptors;

        Assert.NotNull(interceptors);
        Assert.Contains(interceptors, interceptor => interceptor is AuditTimestampsInterceptor);
    }

    [Fact]
    public async Task Migrations_history_keeps_original_column_names()
    {
        var columns = await QueryScalarAsync<string>(
            "SELECT string_agg(column_name::text, ',' ORDER BY column_name) FROM information_schema.columns " +
            "WHERE table_schema = 'identity' AND table_name = '__EFMigrationsHistory'");

        Assert.Equal("MigrationId,ProductVersion", columns);
    }

    [Fact]
    public void Identity_model_uses_snake_case_names()
    {
        using var scope = Api.Services.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<IdentityDbContext>().GetService<IDesignTimeModel>().Model;

        var names = model.GetRelationalModel().Tables.SelectMany(table =>
            new[] { table.Name }
                .Concat(table.Columns.Select(column => column.Name))
                .Concat(table.UniqueConstraints.Select(constraint => constraint.Name))
                .Concat(table.ForeignKeyConstraints.Select(constraint => constraint.Name))
                .Concat(table.Indexes.Select(index => index.Name)));

        Assert.All(names, name => Assert.Equal(SnakeCaseNaming.ToSnakeCase(name), name));
    }
}
