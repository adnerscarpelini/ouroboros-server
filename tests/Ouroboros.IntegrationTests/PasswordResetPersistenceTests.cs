using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ouroboros.Modules.Identity.Application.PasswordResets;
using Ouroboros.Modules.Identity.Application.Security;
using Ouroboros.Modules.Identity.Application.Sessions;
using Ouroboros.Modules.Identity.Contracts.PasswordResets;
using Ouroboros.Modules.Identity.Domain.EmailTokens;
using Ouroboros.Modules.Identity.Domain.Sessions;
using Ouroboros.Modules.Identity.Domain.Users;
using Ouroboros.Modules.Identity.Infrastructure.Persistence;

namespace Ouroboros.IntegrationTests;

public sealed class PasswordResetPersistenceTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private const string Email = "reset-port@example.com";
    private CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Replacement_failure_keeps_the_previous_token_and_recipient()
    {
        var (user, token, _) = await SeedAsync();
        await ExecuteAsync("""
            CREATE FUNCTION fail_reset_insert() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected reset persistence failure'; END $$;
            CREATE TRIGGER fail_reset_insert AFTER INSERT ON identity.email_tokens
            FOR EACH ROW EXECUTE FUNCTION fail_reset_insert();
            """);
        await using var scope = Api.Services.CreateAsyncScope();
        await Assert.ThrowsAsync<DbUpdateException>(() => scope.ServiceProvider.GetRequiredService<IPasswordResetStore>()
            .ReplacePendingTokenAsync(EmailToken.ForPasswordReset(user.Id, user.Email, OpaqueTokens.Generate().Hash,
                DateTimeOffset.UtcNow.AddHours(1)), DateTimeOffset.UtcNow, Cancellation));

        Assert.Equal(1L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.email_tokens"));
        Assert.Null(await QueryScalarAsync<DateTime?>("SELECT revoked_at FROM identity.email_tokens"));
        Assert.Equal(token.Id, await QueryScalarAsync<long>("SELECT id FROM identity.email_tokens"));
        Assert.Equal(Email, await QueryScalarAsync<string>("SELECT email FROM identity.email_tokens"));
    }

    [Fact]
    public async Task Reset_waits_for_rotation_and_revokes_its_committed_successor()
    {
        var (user, _, rawToken) = await SeedAsync();
        var now = DateTimeOffset.UtcNow;
        await using var scope = Api.Services.CreateAsyncScope();
        var sessions = scope.ServiceProvider.GetRequiredService<ISessionStore>();
        var session = Session.Open(user.Id, OpaqueTokens.Generate().Hash, now.AddDays(7));
        sessions.AddSession(session);
        await sessions.SaveChangesAsync(Cancellation);
        await using var transaction = await sessions.BeginTransactionAsync(Cancellation);
        await sessions.LockSessionAsync(session.Id, Cancellation);
        var successor = RefreshToken.Issue(session.Id, OpaqueTokens.Generate().Hash, now.AddDays(7));
        sessions.AddRefreshToken(successor);
        await sessions.SaveChangesAsync(Cancellation);

        await using var resetScope = Api.Services.CreateAsyncScope();
        var reset = resetScope.ServiceProvider.GetRequiredService<ICompletePasswordReset>()
            .ExecuteAsync(new(rawToken, "uma frase diferente para a nova credencial"), Cancellation);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!await QueryScalarAsync<bool>("""
            SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname = current_database()
                AND wait_event_type = 'Lock' AND query LIKE '%identity.sessions%')
            """))
        {
            await Task.Delay(20, timeout.Token);
        }

        await transaction.CommitAsync(Cancellation);
        Assert.Equal(CompletePasswordResetOutcome.Completed, (await reset).Outcome);
        Assert.Equal(0L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.refresh_tokens WHERE revoked_at IS NULL"));
        Assert.NotNull(await QueryScalarAsync<DateTime?>("SELECT used_at FROM identity.email_tokens"));
    }

    [Fact]
    public async Task Completion_writes_require_the_shared_session_transaction()
    {
        var (user, token, _) = await SeedAsync();
        await using var scope = Api.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IPasswordResetStore>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ConsumeTokenAsync(token.Id, DateTimeOffset.UtcNow, Cancellation));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ChangePasswordAsync(user.Id, "new hash", DateTimeOffset.UtcNow, Cancellation));
        Assert.Null(await QueryScalarAsync<DateTime?>("SELECT used_at FROM identity.email_tokens"));
    }

    private async Task<(User User, EmailToken Token, string RawToken)> SeedAsync()
    {
        await using var scope = Api.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var user = User.Create(Email, scope.ServiceProvider.GetRequiredService<IPasswordHasher>()
            .Hash("uma frase antiga para a credencial anterior"), UserRole.Customer);
        context.Users.Add(user);
        await context.SaveChangesAsync(Cancellation);
        var (rawToken, hash) = OpaqueTokens.Generate();
        var token = EmailToken.ForPasswordReset(user.Id, user.Email, hash, DateTimeOffset.UtcNow.AddHours(1));
        context.EmailTokens.Add(token);
        await context.SaveChangesAsync(Cancellation);
        return (user, token, rawToken);
    }
}
