using Microsoft.EntityFrameworkCore;
using Ouroboros.BuildingBlocks.Persistence;
using Ouroboros.Modules.Identity.Domain.Sessions;
using Ouroboros.Modules.Identity.Domain.Users;

namespace Ouroboros.Modules.Identity.Infrastructure.Persistence;

internal sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : DbContext(options)
{
    public const string Schema = "identity";

    public DbSet<User> Users => Set<User>();

    public DbSet<Session> Sessions => Set<Session>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    // Trava a linha da sessão até o fim da transação atual. Toda escrita em refresh_tokens de uma sessão
    // (renovação, revogação por reuso, logout) trava a sessão antes: sem o lock, em READ COMMITTED, uma
    // revogação não enxergaria o token que uma renovação em andamento inseriu e ainda não confirmou.
    public Task LockSessionAsync(long sessionId, CancellationToken cancellationToken) =>
        Database.ExecuteSqlAsync($"SELECT 1 FROM identity.sessions WHERE id = {sessionId} FOR UPDATE", cancellationToken);

    // Trava todas as sessões de um usuário, sempre na ordem do id, para que duas chamadas simultâneas
    // não se bloqueiem em ordens diferentes.
    public Task LockUserSessionsAsync(long userId, CancellationToken cancellationToken) =>
        Database.ExecuteSqlAsync($"SELECT 1 FROM identity.sessions WHERE user_id = {userId} ORDER BY id FOR UPDATE", cancellationToken);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(IdentityDbContext).Assembly);
        SnakeCaseNaming.Apply(modelBuilder);
    }
}
