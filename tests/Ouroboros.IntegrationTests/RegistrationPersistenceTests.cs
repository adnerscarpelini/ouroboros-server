using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ouroboros.Modules.Identity.Application.Registrations;
using Ouroboros.Modules.Identity.Application.Security;
using Ouroboros.Modules.Identity.Domain.EmailTokens;
using Ouroboros.Modules.Identity.Domain.Users;
using Ouroboros.Modules.Identity.Infrastructure.Persistence;

namespace Ouroboros.IntegrationTests;

public sealed class RegistrationPersistenceTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private const string Email = "registration-port@example.com";
    private CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Replacement_failure_restores_the_previous_pending_token()
    {
        var now = DateTimeOffset.UtcNow;
        var token = await SeedAsync(now);
        await ExecuteAsync("""
            CREATE FUNCTION fail_registration_write() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected registration persistence failure'; END $$;
            CREATE TRIGGER fail_registration_write AFTER INSERT ON identity.email_tokens
            FOR EACH ROW EXECUTE FUNCTION fail_registration_write();
            """);

        await using (var scope = Api.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IRegistrationStore>();
            await Assert.ThrowsAsync<DbUpdateException>(() => store.ReplacePendingTokenAsync(
                EmailToken.ForRegistration(Email, OpaqueTokens.Generate().Hash, now.AddHours(1)), now, Cancellation));
        }

        Assert.Equal(1L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.email_tokens"));
        Assert.Null(await QueryScalarAsync<DateTime?>("SELECT revoked_at FROM identity.email_tokens"));
        Assert.Equal(token.Id, await QueryScalarAsync<long>("SELECT id FROM identity.email_tokens"));
    }

    [Fact]
    public async Task Account_write_failure_rolls_back_consumption_and_allows_retry()
    {
        var now = DateTimeOffset.UtcNow;
        var token = await SeedAsync(now);
        await ExecuteAsync("""
            CREATE FUNCTION fail_registration_user() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected account persistence failure'; END $$;
            CREATE TRIGGER fail_registration_user AFTER INSERT ON identity.users
            FOR EACH ROW EXECUTE FUNCTION fail_registration_user();
            """);
        await using (var scope = Api.Services.CreateAsyncScope())
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => scope.ServiceProvider.GetRequiredService<IRegistrationStore>()
                .CompleteAsync(token.Id, User.Create(Email, "hash", UserRole.Customer), now, Cancellation));
        }

        Assert.Null(await QueryScalarAsync<DateTime?>("SELECT used_at FROM identity.email_tokens"));
        Assert.Equal(0L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.users"));
        await ExecuteAsync("DROP TRIGGER fail_registration_user ON identity.users");
        await using var retry = Api.Services.CreateAsyncScope();
        Assert.True(await retry.ServiceProvider.GetRequiredService<IRegistrationStore>()
            .CompleteAsync(token.Id, User.Create(Email, "hash", UserRole.Customer), now, Cancellation));
    }

    [Fact]
    public async Task Competing_accounts_do_not_consume_the_losing_token()
    {
        var now = DateTimeOffset.UtcNow;
        var first = await SeedAsync(now);
        var second = await SeedAsync(now);
        await using var firstScope = Api.Services.CreateAsyncScope();
        await using var secondScope = Api.Services.CreateAsyncScope();
        var results = await Task.WhenAll(
            firstScope.ServiceProvider.GetRequiredService<IRegistrationStore>()
                .CompleteAsync(first.Id, User.Create(Email, "first", UserRole.Customer), now, Cancellation),
            secondScope.ServiceProvider.GetRequiredService<IRegistrationStore>()
                .CompleteAsync(second.Id, User.Create(Email, "second", UserRole.Customer), now, Cancellation));

        Assert.Single(results, completed => completed);
        Assert.Equal(1L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.users"));
        Assert.Equal(1L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.email_tokens WHERE used_at IS NOT NULL"));
        var losingId = results[0] ? second.Id : first.Id;
        Assert.Null(await QueryScalarAsync<DateTime?>($"SELECT used_at FROM identity.email_tokens WHERE id = {losingId}"));
    }

    private async Task<EmailToken> SeedAsync(DateTimeOffset now)
    {
        await using var scope = Api.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var token = EmailToken.ForRegistration(Email, OpaqueTokens.Generate().Hash, now.AddHours(1));
        context.EmailTokens.Add(token);
        await context.SaveChangesAsync(Cancellation);
        return token;
    }
}
