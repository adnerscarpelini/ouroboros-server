using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ouroboros.Identity.Application.Security;
using Ouroboros.Identity.Application.Sessions;
using Ouroboros.Identity.Domain.Sessions;
using Ouroboros.Identity.Domain.Users;
using Ouroboros.Identity.Infrastructure.Persistence;
using Ouroboros.Identity.Infrastructure.Security;

namespace Ouroboros.IntegrationTests;

// Falhas reais no PostgreSQL e coordenação pela porta usada pelos casos de uso.
public sealed class SessionPersistenceTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private const string Email = "session-port@example.com";
    private const string Password = "uma frase longa para testar a persistencia";
    private const string SessionsRoute = "/api/v1/identity/sessions";
    private CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Login_write_failure_rolls_back_rehash_counter_and_session()
    {
        var originalHash = await CreateUserAsync();
        await ExecuteAsync("UPDATE identity.users SET failed_login_count = 3");
        await FailTokenWritesAsync("INSERT");

        using var response = await Client.PostAsJsonAsync(SessionsRoute, new { Email, Password }, Cancellation);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(originalHash, await QueryScalarAsync<string>("SELECT password_hash FROM identity.users"));
        Assert.Equal(3, await QueryScalarAsync<int>("SELECT failed_login_count FROM identity.users"));
        Assert.Equal(0L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.sessions"));
        Assert.Equal(0L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.refresh_tokens"));
        await ExecuteAsync("DROP TRIGGER fail_token_write ON identity.refresh_tokens");
        await LoginAsync();
        Assert.NotEqual(originalHash, await QueryScalarAsync<string>("SELECT password_hash FROM identity.users"));
    }

    [Fact]
    public async Task Refresh_write_failure_rolls_back_consumption_of_the_previous_token()
    {
        await CreateUserAsync();
        var login = await LoginAsync();
        var token = login.GetProperty("refreshToken").GetString()!;
        await FailTokenWritesAsync("INSERT");

        using var response = await PostTokenAsync("refresh", token);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(1L, await ActiveTokensAsync());
        Assert.Equal(1L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.refresh_tokens"));
        await ExecuteAsync("DROP TRIGGER fail_token_write ON identity.refresh_tokens");
        using var retry = await PostTokenAsync("refresh", token);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(1L, await ActiveTokensAsync());
    }

    [Theory]
    [InlineData("logout")]
    [InlineData("logout-all")]
    [InlineData("reuse")]
    public async Task Revocation_write_failure_keeps_tokens_and_can_be_retried(string operation)
    {
        await CreateUserAsync();
        var login = await LoginAsync();
        var token = login.GetProperty("refreshToken").GetString()!;
        if (operation == "reuse")
        {
            using var rotated = await PostTokenAsync("refresh", token);
            Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        }
        else if (operation == "logout-all")
        {
            await LoginAsync();
        }

        var activeBefore = await ActiveTokensAsync();
        await FailTokenWritesAsync("UPDATE");
        using var response = await RevokeAsync(operation, login, token);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(activeBefore, await ActiveTokensAsync());
        await ExecuteAsync("DROP TRIGGER fail_token_write ON identity.refresh_tokens");
        using var retry = await RevokeAsync(operation, login, token);
        Assert.Equal(operation == "reuse" ? HttpStatusCode.Unauthorized : HttpStatusCode.NoContent, retry.StatusCode);
        Assert.Equal(0L, await ActiveTokensAsync());
    }

    [Fact]
    public async Task Logout_all_waits_for_rotation_and_revokes_the_committed_successor()
    {
        await CreateUserAsync();
        var login = await LoginAsync();
        await using var scope = Api.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ISessionStore>();
        var found = await store.FindRefreshTokenAsync(OpaqueTokens.Hash(login.GetProperty("refreshToken").GetString()!), Cancellation);
        Assert.NotNull(found);
        await using var transaction = await store.BeginTransactionAsync(Cancellation);
        await store.LockSessionAsync(found.Token.SessionId, Cancellation);
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(1, await store.RevokeTokenAsync(found.Token.Id, now, Cancellation));
        store.AddRefreshToken(RefreshToken.Issue(found.Token.SessionId, OpaqueTokens.Generate().Hash, now.AddDays(7)));
        await store.SaveChangesAsync(Cancellation);

        var logout = RevokeAsync("logout-all", login, "");
        await WaitForSessionLockAsync();
        await transaction.CommitAsync(Cancellation);

        using var response = await logout;
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0L, await ActiveTokensAsync());
    }

    [Theory]
    [InlineData("logout")]
    [InlineData("logout-all")]
    public async Task Logout_keeps_the_access_token_valid_until_expiration(string operation)
    {
        await CreateUserAsync();
        var login = await LoginAsync();
        var token = login.GetProperty("refreshToken").GetString()!;
        using var logout = await RevokeAsync(operation, login, token);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/identity/me");
        request.Headers.Authorization = new("Bearer", login.GetProperty("accessToken").GetString());
        using var me = await Client.SendAsync(request, Cancellation);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using var refresh = await PostTokenAsync("refresh", token);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task Logout_all_does_not_wait_for_the_user_or_end_an_uncommitted_login()
    {
        await CreateUserAsync();
        var login = await LoginAsync();
        await using var scope = Api.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ISessionStore>();
        var user = await store.FindUserAsync(Email, Cancellation);
        Assert.NotNull(user);
        await using var transaction = await store.BeginTransactionAsync(Cancellation);
        await store.LockUserAsync(user.Id, Cancellation);
        var (token, hash) = OpaqueTokens.Generate();
        store.AddSession(Session.Open(user.Id, hash, DateTimeOffset.UtcNow.AddDays(7)));
        await store.SaveChangesAsync(Cancellation);

        // O logout-all não pede o lock do usuário e não enxerga a sessão ainda não confirmada.
        using var logout = await RevokeAsync("logout-all", login, "").WaitAsync(TimeSpan.FromSeconds(10), Cancellation);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(0L, await ActiveTokensAsync());
        await transaction.CommitAsync(Cancellation);
        Assert.Equal(1L, await ActiveTokensAsync());
        using var refresh = await PostTokenAsync("refresh", token);
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
    }

    [Fact]
    public async Task Logout_winning_the_session_lock_prevents_a_concurrent_refresh()
    {
        await CreateUserAsync();
        var login = await LoginAsync();
        var token = login.GetProperty("refreshToken").GetString()!;
        await using var scope = Api.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ISessionStore>();
        var found = await store.FindRefreshTokenAsync(OpaqueTokens.Hash(token), Cancellation);
        Assert.NotNull(found);
        await using var transaction = await store.BeginTransactionAsync(Cancellation);
        await store.LockSessionAsync(found.Token.SessionId, Cancellation);

        var logout = PostTokenAsync("logout", token);
        await WaitForSessionLockAsync();
        var refresh = PostTokenAsync("refresh", token);
        await WaitForSessionLockAsync(expectedWaiters: 2);
        await transaction.CommitAsync(Cancellation);

        using var logoutResponse = await logout;
        using var refreshResponse = await refresh;
        Assert.Equal(HttpStatusCode.NoContent, logoutResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, refreshResponse.StatusCode);
        Assert.Equal(0L, await ActiveTokensAsync());
    }

    private async Task WaitForSessionLockAsync(int expectedWaiters = 1)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!await QueryScalarAsync<bool>($"""
            SELECT count(*) >= {expectedWaiters} FROM pg_stat_activity
                WHERE datname = current_database() AND wait_event_type = 'Lock'
                  AND query LIKE '%identity.sessions%'
            """))
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private Task FailTokenWritesAsync(string operation) => ExecuteAsync($"""
        CREATE FUNCTION fail_token_write() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN RAISE EXCEPTION 'injected session persistence failure'; END $$;
        CREATE TRIGGER fail_token_write AFTER {operation} ON identity.refresh_tokens
        FOR EACH ROW EXECUTE FUNCTION fail_token_write();
        """);

    private async Task<string> CreateUserAsync()
    {
        // O hasher real da API pede rehash deste hash antigo.
        var hash = new AspNetCorePasswordHasher(iterationCount: 1_000).Hash(Password);
        await using var scope = Api.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        context.Users.Add(User.Create(Email, hash, UserRole.Customer));
        await context.SaveChangesAsync(Cancellation);
        return hash;
    }

    private async Task<JsonElement> LoginAsync()
    {
        using var response = await Client.PostAsJsonAsync(SessionsRoute, new { Email, Password }, Cancellation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Cancellation);
    }

    private Task<HttpResponseMessage> PostTokenAsync(string operation, string token) =>
        Client.PostAsJsonAsync($"{SessionsRoute}/{operation}", new { RefreshToken = token }, Cancellation);

    private async Task<HttpResponseMessage> RevokeAsync(string operation, JsonElement login, string token)
    {
        if (operation != "logout-all")
        {
            return await PostTokenAsync(operation == "reuse" ? "refresh" : operation, token);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{SessionsRoute}/logout-all");
        request.Headers.Authorization = new("Bearer", login.GetProperty("accessToken").GetString());
        return await Client.SendAsync(request, Cancellation);
    }

    private Task<long> ActiveTokensAsync() =>
        QueryScalarAsync<long>("SELECT count(*) FROM identity.refresh_tokens WHERE revoked_at IS NULL");
}
