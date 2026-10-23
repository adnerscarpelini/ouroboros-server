using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Ouroboros.BuildingBlocks.Persistence;

namespace Ouroboros.IntegrationTests;

// Testes de unidade da convenção de nomes; não usam banco, só montam o modelo do EF.
public sealed class SnakeCaseNamingTests
{
    [Theory]
    [InlineData("ExternalId", "external_id")]
    [InlineData("CreatedAt", "created_at")]
    [InlineData("PK_Users", "pk_users")]
    [InlineData("IX_users_ExternalId", "ix_users_external_id")]
    [InlineData("HTTPStatus", "http_status")]
    [InlineData("Id", "id")]
    public void Converts_identifiers(string name, string expected) =>
        Assert.Equal(expected, SnakeCaseNaming.ToSnakeCase(name));

    [Fact]
    public void Model_with_owned_types_and_relationships_builds_with_snake_case_names()
    {
        using var context = new SampleDbContext();

        var tables = context.Model.GetRelationalModel().Tables.ToDictionary(table => table.Name);

        var customers = tables["customer"];
        Assert.Contains(customers.Columns, column => column.Name == "id");
        Assert.Contains(customers.Columns, column => column.Name == "full_name");
        Assert.Contains(customers.Columns, column => column.Name == "home_address_street_name");
        Assert.Equal("pk_customer", customers.PrimaryKey!.Name);

        var phones = tables["phone_number"];
        Assert.Contains(phones.Columns, column => column.Name == "customer_id");
        Assert.Contains(phones.Columns, column => column.Name == "number");

        var orders = tables["order"];
        Assert.Contains(orders.ForeignKeyConstraints, constraint => constraint.Name == "fk_order_customer_customer_id");
        Assert.Contains(orders.Indexes, index => index.Name == "ix_order_order_code");

        Assert.True(tables.ContainsKey("customer_tag"));
    }

    private sealed class Customer
    {
        public int Id { get; set; }

        public string FullName { get; set; } = "";

        public Address HomeAddress { get; set; } = new();

        public List<PhoneNumber> Phones { get; set; } = [];

        public List<Tag> Tags { get; set; } = [];
    }

    private sealed class Address
    {
        public string StreetName { get; set; } = "";
    }

    private sealed class PhoneNumber
    {
        public string Number { get; set; } = "";
    }

    private sealed class Order
    {
        public int Id { get; set; }

        public string OrderCode { get; set; } = "";

        public Customer Customer { get; set; } = null!;
    }

    private sealed class Tag
    {
        public int Id { get; set; }

        public List<Customer> Customers { get; set; } = [];
    }

    private sealed class SampleDbContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
            optionsBuilder.UseNpgsql("Host=localhost;Database=model_only");

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Customer>(customer =>
            {
                customer.OwnsOne(entity => entity.HomeAddress);
                customer.OwnsMany(entity => entity.Phones);
                customer.HasMany(entity => entity.Tags).WithMany(tag => tag.Customers);
            });
            modelBuilder.Entity<Order>().HasIndex(order => order.OrderCode).IsUnique();
            modelBuilder.Entity<Tag>();

            SnakeCaseNaming.Apply(modelBuilder);
        }
    }
}
