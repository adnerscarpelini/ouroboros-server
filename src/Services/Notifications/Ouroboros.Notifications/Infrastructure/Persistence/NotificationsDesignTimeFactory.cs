using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ouroboros.Notifications.Infrastructure.Persistence;

internal sealed class NotificationsDesignTimeFactory : IDesignTimeDbContextFactory<NotificationsDbContext>
{
    public NotificationsDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Notifications");
        if (string.IsNullOrWhiteSpace(connection)) { throw new InvalidOperationException("Configure ConnectionStrings__Notifications para migrations de Notifications."); }
        return new NotificationsDbContext(new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseNpgsql(connection, postgres => postgres.MigrationsHistoryTable("__EFMigrationsHistory", NotificationsDbContext.Schema)).Options);
    }
}
