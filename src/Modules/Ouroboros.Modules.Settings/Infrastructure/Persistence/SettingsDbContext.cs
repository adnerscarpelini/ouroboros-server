using Microsoft.EntityFrameworkCore;
using Ouroboros.BuildingBlocks.Persistence;
using Ouroboros.Modules.Settings.Domain.Parameters;

namespace Ouroboros.Modules.Settings.Infrastructure.Persistence;

internal sealed class SettingsDbContext(DbContextOptions<SettingsDbContext> options) : DbContext(options)
{
    public const string Schema = "settings";

    public DbSet<Parameter> Parameters => Set<Parameter>();

    public DbSet<ParameterHistory> ParameterHistory => Set<ParameterHistory>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SettingsDbContext).Assembly);
        SnakeCaseNaming.Apply(modelBuilder);
    }
}
