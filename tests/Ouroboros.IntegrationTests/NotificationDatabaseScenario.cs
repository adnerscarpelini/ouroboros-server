using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Ouroboros.Messaging;
using Ouroboros.Messaging.Contracts;
using Ouroboros.Notifications;
using Ouroboros.Notifications.Application;
using Ouroboros.Notifications.Domain;
using Ouroboros.Notifications.Infrastructure;
using Ouroboros.Notifications.Infrastructure.Persistence;

namespace Ouroboros.IntegrationTests;

internal sealed class NotificationDatabaseScenario : IAsyncDisposable
{
    internal readonly NotificationTestKeys Keys = new();
    internal readonly FakeTimeProvider Clock = new(DateTimeOffset.UtcNow);
    internal IHost Host { get; private set; } = null!;
    internal IServiceProvider Services => Host.Services;
    private readonly ProvisionedInstance _database;
    private readonly IEmailTransport? _transport;

    private NotificationDatabaseScenario(ProvisionedInstance database, IEmailTransport? transport) { _database = database; _transport = transport; }

    internal static async Task<NotificationDatabaseScenario> CreateAsync(ProvisionedInstance database, IEmailTransport? transport = null)
    {
        await ResetAsync(database);
        var scenario = new NotificationDatabaseScenario(database, transport);
        scenario.BuildHost();
        return scenario;
    }

    internal static async Task ResetAsync(ProvisionedInstance database)
    {
        await database.ExecuteAdminAsync(ProvisionedInstance.NotificationsDatabase, "DROP SCHEMA IF EXISTS notifications CASCADE");
        await using var migration = Context(database, ProvisionedInstance.NotificationsMigrator);
        await migration.Database.MigrateAsync(TestContext.Current.CancellationToken);
    }

    internal static NotificationsDbContext Context(ProvisionedInstance database, string role = ProvisionedInstance.NotificationsRuntime) =>
        new(new DbContextOptionsBuilder<NotificationsDbContext>().UseNpgsql(database.ConnectionStringFor(role, ProvisionedInstance.NotificationsDatabase),
            postgres => postgres.MigrationsHistoryTable("__EFMigrationsHistory", NotificationsDbContext.Schema)).Options);

    internal NotificationsDbContext Context() => Context(_database);

    internal MessageEnvelope Message(string? alias = null, DateTimeOffset? occurred = null, DateTimeOffset? expires = null, Guid? id = null) =>
        Keys.Message(changeMetadata: metadata => metadata with
        {
            MessageId = id ?? Guid.CreateVersion7(),
            SupersessionKey = alias ?? Guid.NewGuid().ToString("N"),
            OccurredAt = occurred ?? Clock.GetUtcNow(),
            ExpiresAt = expires ?? Clock.GetUtcNow().AddHours(4),
        });

    internal async Task<AcceptanceResult> AcceptAsync(MessageEnvelope message) => await AcceptAsync(EnvelopeCodec.Encode(message));

    internal async Task<AcceptanceResult> AcceptAsync(byte[] body)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<InboxAcceptance>().AcceptAsync(body, null, TestContext.Current.CancellationToken);
    }

    internal async Task RunAsync()
    {
        using var executor = new InboxExecutor(Services.GetRequiredService<IServiceScopeFactory>(),
            Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<NotificationOptions>>(), Clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<InboxExecutor>.Instance);
        await executor.RunOnceAsync(TestContext.Current.CancellationToken);
    }

    internal async Task<InboxMessage> RowAsync(Guid id)
    {
        await using var context = Context();
        return await context.Inbox.AsNoTracking().SingleAsync(row => row.MessageId == id, TestContext.Current.CancellationToken);
    }

    internal async Task RestartAsync() { await Host.StopAsync(); Host.Dispose(); BuildHost(); }

    private void BuildHost()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        { EnvironmentName = Environments.Development, ContentRootPath = Keys.DirectoryPath, ApplicationName = typeof(NotificationDatabaseScenario).Assembly.GetName().Name });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Notifications"] = _database.ConnectionStringFor(ProvisionedInstance.NotificationsRuntime, ProvisionedInstance.NotificationsDatabase),
            ["Notifications:Consumer:Enabled"] = "false",
            ["Notifications:Email:Provider"] = "PickupDirectory",
            ["Notifications:Email:PickupDirectory"] = Keys.EmailsPath,
            ["Notifications:PrivateKeyRingPath"] = Keys.RingPath,
            ["Notifications:FingerprintKeyRingPath"] = Keys.FingerprintRingPath,
        });
        builder.Services.AddNotificationsService();
        builder.Services.AddSingleton<TimeProvider>(Clock);
        if (_transport is not null) { builder.Services.AddSingleton(_transport); }
        Host = builder.Build();
    }

    public async ValueTask DisposeAsync()
    {
        await Host.StopAsync(); Host.Dispose(); Keys.Dispose();
    }
}
