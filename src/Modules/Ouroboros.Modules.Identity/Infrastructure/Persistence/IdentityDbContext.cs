using Microsoft.EntityFrameworkCore;
using Ouroboros.Modules.Identity.Domain.Users;

namespace Ouroboros.Modules.Identity.Infrastructure.Persistence;

internal sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : DbContext(options)
{
    public const string Schema = "identity";

    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(IdentityDbContext).Assembly);
        SnakeCaseNaming.Apply(modelBuilder);
    }
}
