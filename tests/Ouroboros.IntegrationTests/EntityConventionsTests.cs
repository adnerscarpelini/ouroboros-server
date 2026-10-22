using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Ouroboros.Modules.Identity.Domain;
using Ouroboros.Modules.Identity.Infrastructure.Persistence;

namespace Ouroboros.IntegrationTests;

// Exercita a base Entity, o mapeamento comum e o interceptor de datas do Identity com uma entidade
// de teste, contra o PostgreSQL real.
public sealed class EntityConventionsTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Start);

    [Fact]
    public async Task New_entity_gets_equal_created_and_updated_from_time_provider()
    {
        await using (var context = await CreateContextAsync())
        {
            context.Widgets.Add(new Widget { Name = "novo" });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var reader = await CreateContextAsync(createTables: false);
        var saved = await reader.Widgets.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(Start, saved.CreatedAt);
        Assert.Equal(Start, saved.UpdatedAt);
        Assert.Equal(TimeSpan.Zero, saved.CreatedAt.Offset);
    }

    [Fact]
    public async Task Changed_entity_only_updates_updated_at()
    {
        await using (var context = await CreateContextAsync())
        {
            context.Widgets.Add(new Widget { Name = "original" });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        _time.Advance(TimeSpan.FromMinutes(5));
        await using (var context = await CreateContextAsync(createTables: false))
        {
            var widget = await context.Widgets.SingleAsync(TestContext.Current.CancellationToken);
            widget.Name = "alterado";
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var reader = await CreateContextAsync(createTables: false);
        var saved = await reader.Widgets.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(Start, saved.CreatedAt);
        Assert.Equal(Start.AddMinutes(5), saved.UpdatedAt);
    }

    [Fact]
    public async Task Created_at_is_not_overwritten_on_update()
    {
        await using (var context = await CreateContextAsync())
        {
            context.Widgets.Add(new Widget { Name = "original" });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        _time.Advance(TimeSpan.FromMinutes(5));
        await using (var context = await CreateContextAsync(createTables: false))
        {
            var widget = await context.Widgets.SingleAsync(TestContext.Current.CancellationToken);
            widget.Name = "alterado";
            context.Entry(widget).Property(entity => entity.CreatedAt).CurrentValue = Start.AddYears(5);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var reader = await CreateContextAsync(createTables: false);
        Assert.Equal(Start, (await reader.Widgets.SingleAsync(TestContext.Current.CancellationToken)).CreatedAt);
    }

    // Só o value object (owned) muda; a entidade dona deve ter o UpdatedAt atualizado.
    [Theory]
    [InlineData("owned-one")]
    [InlineData("owned-many-added")]
    [InlineData("owned-many-removed")]
    public async Task Changing_only_owned_values_updates_owner_updated_at(string change)
    {
        await using (var context = await CreateContextAsync())
        {
            context.Widgets.Add(new Widget { Name = "dono", Size = new Dimensions { Width = 1 }, Parts = [new Part { Code = "a" }] });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        _time.Advance(TimeSpan.FromMinutes(1));
        await using (var context = await CreateContextAsync(createTables: false))
        {
            var widget = await context.Widgets.SingleAsync(TestContext.Current.CancellationToken);
            switch (change)
            {
                case "owned-one":
                    widget.Size.Width = 2;
                    break;
                case "owned-many-added":
                    widget.Parts.Add(new Part { Code = "b" });
                    break;
                default:
                    widget.Parts.Clear();
                    break;
            }

            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var reader = await CreateContextAsync(createTables: false);
        var saved = await reader.Widgets.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(Start, saved.CreatedAt);
        Assert.Equal(Start.AddMinutes(1), saved.UpdatedAt);
    }

    [Fact]
    public async Task External_id_is_uuid_v7_with_unique_index()
    {
        Guid externalId;
        await using (var context = await CreateContextAsync())
        {
            var widget = new Widget { Name = "a" };
            context.Widgets.Add(widget);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            externalId = widget.ExternalId;
        }

        Assert.Equal(7, externalId.Version);
        var uniqueIndex = await QueryScalarAsync<string>(
            "SELECT indexdef FROM pg_indexes WHERE schemaname = 'conventions_tests' AND tablename = 'widgets' " +
            "AND indexdef LIKE 'CREATE UNIQUE INDEX%(external_id)%'");
        Assert.NotNull(uniqueIndex);

        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "INSERT INTO conventions_tests.widgets (external_id, name, size_width, created_at, updated_at) " +
            $"VALUES ('{externalId}', 'b', 1, now(), now())"));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
    }

    [Fact]
    public async Task Columns_follow_the_identifier_and_date_convention()
    {
        await using (await CreateContextAsync())
        {
        }

        Assert.Equal("ALWAYS", await ColumnAsync<string>("id", "identity_generation"));
        Assert.Equal("bigint", await ColumnAsync<string>("id", "data_type"));
        Assert.Equal("uuid", await ColumnAsync<string>("external_id", "data_type"));
        Assert.Equal("NO", await ColumnAsync<string>("external_id", "is_nullable"));
        Assert.Equal("timestamp with time zone", await ColumnAsync<string>("created_at", "data_type"));
        Assert.Equal("timestamp with time zone", await ColumnAsync<string>("updated_at", "data_type"));
    }

    private Task<T?> ColumnAsync<T>(string column, string attribute) => QueryScalarAsync<T>(
        $"SELECT {attribute}::text FROM information_schema.columns " +
        $"WHERE table_schema = 'conventions_tests' AND table_name = 'widgets' AND column_name = '{column}'");

    private async Task<ConventionsDbContext> CreateContextAsync(bool createTables = true)
    {
        var options = new DbContextOptionsBuilder<ConventionsDbContext>()
            .UseNpgsql(ConnectionString)
            .AddInterceptors(new AuditTimestampsInterceptor(_time))
            .Options;
        var context = new ConventionsDbContext(options);
        if (createTables)
        {
            await context.GetService<IRelationalDatabaseCreator>().CreateTablesAsync(TestContext.Current.CancellationToken);
        }

        return context;
    }

    private sealed class Widget : Entity
    {
        public string Name { get; set; } = "";

        public Dimensions Size { get; set; } = new();

        public List<Part> Parts { get; set; } = [];
    }

    private sealed class Dimensions
    {
        public int Width { get; set; }
    }

    private sealed class Part
    {
        public string Code { get; set; } = "";
    }

    private sealed class WidgetConfiguration : EntityConfiguration<Widget>
    {
        protected override void ConfigureEntity(EntityTypeBuilder<Widget> builder)
        {
            builder.ToTable("widgets", "conventions_tests");
            builder.OwnsOne(widget => widget.Size);
            builder.OwnsMany(widget => widget.Parts, parts => parts.ToTable("widget_parts", "conventions_tests"));
        }
    }

    private sealed class ConventionsDbContext(DbContextOptions<ConventionsDbContext> options) : DbContext(options)
    {
        public DbSet<Widget> Widgets => Set<Widget>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            new WidgetConfiguration().Configure(modelBuilder.Entity<Widget>());
            SnakeCaseNaming.Apply(modelBuilder);
        }
    }
}
