using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ouroboros.Modules.Identity.Domain.Users;
using Ouroboros.Modules.Identity.Infrastructure.Persistence;

namespace Ouroboros.IntegrationTests;

public sealed class UserPersistenceTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    [Fact]
    public async Task User_is_saved_with_normalized_email_and_role_as_text()
    {
        await using (var scope = Api.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            context.Users.Add(User.Create("  Cliente@Example.COM ", "hash", UserRole.Customer));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal("cliente@example.com", await QueryScalarAsync<string>("SELECT email FROM identity.users"));
        Assert.Equal("Customer", await QueryScalarAsync<string>("SELECT role FROM identity.users"));
        Assert.True(await QueryScalarAsync<bool>("SELECT is_active FROM identity.users"));
    }

    [Fact]
    public async Task Email_is_unique()
    {
        await using var scope = Api.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        context.Users.Add(User.Create("dono@example.com", "hash", UserRole.Customer));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.Users.Add(User.Create("DONO@example.com", "outro-hash", UserRole.Admin));
        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => context.SaveChangesAsync(TestContext.Current.CancellationToken));

        var postgresException = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgresException.SqlState);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void User_requires_email(string email) =>
        Assert.ThrowsAny<ArgumentException>(() => User.Create(email, "hash", UserRole.Customer));

    [Fact]
    public void User_rejects_email_longer_than_limit() =>
        Assert.Throws<ArgumentException>(() => User.Create(new string('a', 250) + "@x.io", "hash", UserRole.Customer));
}
