using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Ouroboros.Identity.Application.Security;
using Ouroboros.Identity.Domain.Users;
using Ouroboros.Identity.Infrastructure.Persistence;

namespace Ouroboros.IntegrationTests;

public sealed class ChangePasswordTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private const string Password = "uma frase antiga longa e exclusiva";
    private const string NewPassword = "outra frase nova longa e exclusiva";
    private const string Route = "/api/v1/identity/me/password";
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Changes_password_and_revokes_other_sessions_but_current_session_keeps_refreshing()
    {
        await SeedAsync();
        var first = await LoginAsync(Password);
        var second = await LoginAsync(Password);
        await ExecuteAsync("UPDATE identity.users SET failed_login_count = 2, locked_until = now() - interval '1 minute'");
        var response = await ChangeAsync(first, Password, NewPassword);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, await QueryScalarAsync<int>("SELECT failed_login_count FROM identity.users"));
        Assert.Equal(0L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.users WHERE locked_until IS NOT NULL"));
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(first)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(second)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginResponseAsync(Password)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginResponseAsync(NewPassword)).StatusCode);
        Assert.Equal(0, await QueryScalarAsync<int>("SELECT failed_login_count FROM identity.users"));
    }

    [Fact]
    public async Task Wrong_current_password_counts_failure_and_locked_account_returns_same_generic_401()
    {
        await SeedAsync();
        var session = await LoginAsync(Password);
        var response = await ChangeAsync(session, "incorreta", NewPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("E-mail ou senha inválidos.", await response.Content.ReadAsStringAsync(Cancellation));
        Assert.Equal(1, await QueryScalarAsync<int>("SELECT failed_login_count FROM identity.users"));
        await ExecuteAsync("UPDATE identity.users SET locked_until = now() + interval '1 hour'");
        var locked = await ChangeAsync(session, Password, NewPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        Assert.Contains("E-mail ou senha inválidos.", await locked.Content.ReadAsStringAsync(Cancellation));
        Assert.Equal(1, await QueryScalarAsync<int>("SELECT failed_login_count FROM identity.users"));
    }

    [Theory]
    [InlineData(Password)]
    [InlineData("curta")]
    public async Task Rejected_new_password_changes_nothing_and_does_not_count_as_authentication_failure(string password)
    {
        await SeedAsync();
        var session = await LoginAsync(Password);
        var hash = await QueryScalarAsync<string>("SELECT password_hash FROM identity.users");
        Assert.Equal(HttpStatusCode.BadRequest, (await ChangeAsync(session, Password, password)).StatusCode);
        Assert.Equal(hash, await QueryScalarAsync<string>("SELECT password_hash FROM identity.users"));
        Assert.Equal(0, await QueryScalarAsync<int>("SELECT failed_login_count FROM identity.users"));
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(session)).StatusCode);
    }

    [Fact]
    public async Task Missing_token_is_unauthorized()
    {
        using var response = await Client.PutAsJsonAsync(Route, new { CurrentPassword = Password, NewPassword }, Cancellation);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Failure_revoking_tokens_rolls_back_password_and_lockout_reset()
    {
        await SeedAsync();
        var first = await LoginAsync(Password);
        var second = await LoginAsync(Password);
        var hash = await QueryScalarAsync<string>("SELECT password_hash FROM identity.users");
        await ExecuteAsync("""
            UPDATE identity.users SET failed_login_count = 2;
            CREATE FUNCTION fail_password_revocation() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'falha de escrita de teste'; END $$;
            CREATE TRIGGER fail_password_revocation BEFORE UPDATE ON identity.refresh_tokens
            FOR EACH ROW EXECUTE FUNCTION fail_password_revocation();
            """);
        Assert.Equal(HttpStatusCode.InternalServerError, (await ChangeAsync(first, Password, NewPassword)).StatusCode);
        Assert.Equal(hash, await QueryScalarAsync<string>("SELECT password_hash FROM identity.users"));
        Assert.Equal(2, await QueryScalarAsync<int>("SELECT failed_login_count FROM identity.users"));
        Assert.Equal(0L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.refresh_tokens WHERE revoked_at IS NOT NULL"));
        await ExecuteAsync("DROP TRIGGER fail_password_revocation ON identity.refresh_tokens");
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(second)).StatusCode);
    }

    [Fact]
    public async Task Concurrent_changes_using_old_password_allow_only_one_commit()
    {
        await SeedAsync();
        var session = await LoginAsync(Password);
        var responses = await Task.WhenAll(ChangeAsync(session, Password, NewPassword),
            ChangeAsync(session, Password, "uma terceira frase longa e exclusiva"));
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.NoContent);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Unauthorized);
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(session)).StatusCode);
    }

    [Fact]
    public async Task Password_change_rate_limit_rejects_eleventh_request_without_modifying_credentials()
    {
        await SeedAsync();
        var session = await LoginAsync(Password);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await ChangeAsync(session, Password, Password)).StatusCode);
        }

        var response = await ChangeAsync(session, Password, NewPassword);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
        Assert.Equal(HttpStatusCode.OK, (await LoginResponseAsync(Password)).StatusCode);
        Assert.Equal(0, await QueryScalarAsync<int>("SELECT failed_login_count FROM identity.users"));
    }

    [Fact]
    public async Task Changing_own_password_preserves_other_users_credentials_and_sessions()
    {
        await SeedAsync();
        await using (var scope = Api.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            var hash = scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(Password);
            db.Users.Add(User.Create("terceiro@example.com", hash, UserRole.Customer));
            await db.SaveChangesAsync(Cancellation);
        }

        var session = await LoginAsync(Password);
        using var thirdLogin = await Client.PostAsJsonAsync("/api/v1/identity/sessions",
            new { Email = "terceiro@example.com", Password }, Cancellation);
        Assert.Equal(HttpStatusCode.OK, thirdLogin.StatusCode);
        var thirdSession = await thirdLogin.Content.ReadFromJsonAsync<JsonElement>(Cancellation);
        var hashBefore = await QueryScalarAsync<string>("SELECT password_hash FROM identity.users WHERE email = 'terceiro@example.com'");
        Assert.Equal(HttpStatusCode.NoContent, (await ChangeAsync(session, Password, NewPassword)).StatusCode);
        Assert.Equal(hashBefore, await QueryScalarAsync<string>("SELECT password_hash FROM identity.users WHERE email = 'terceiro@example.com'"));
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(thirdSession)).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Wrong_audience_or_changed_database_role_cannot_change_password(bool wrongAudience)
    {
        await SeedAsync();
        var session = await LoginAsync(Password);
        var hash = await QueryScalarAsync<string>("SELECT password_hash FROM identity.users");
        string? token;
        if (wrongAudience)
        {
            var userId = await QueryScalarAsync<Guid>("SELECT external_id FROM identity.users");
            var sessionId = await QueryScalarAsync<Guid>("SELECT external_id FROM identity.sessions");
            token = Api.Services.GetRequiredService<IAccessTokenIssuer>().Issue(userId, UserRole.Admin, sessionId).Token;
        }
        else
        {
            await ExecuteAsync("UPDATE identity.users SET role = 'Admin'");
            token = session.GetProperty("accessToken").GetString();
        }

        using var request = new HttpRequestMessage(HttpMethod.Put, Route)
        {
            Content = JsonContent.Create(new { CurrentPassword = Password, NewPassword }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.SendAsync(request, Cancellation)).StatusCode);
        Assert.Equal(hash, await QueryScalarAsync<string>("SELECT password_hash FROM identity.users"));
    }

    private async Task SeedAsync()
    {
        await using var scope = Api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var hash = scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(Password);
        db.Users.Add(User.Create("cliente@example.com", hash, UserRole.Customer));
        await db.SaveChangesAsync(Cancellation);
    }

    private Task<HttpResponseMessage> LoginResponseAsync(string password) =>
        Client.PostAsJsonAsync("/api/v1/identity/sessions", new { Email = "cliente@example.com", Password = password }, Cancellation);

    private async Task<JsonElement> LoginAsync(string password)
    {
        using var response = await LoginResponseAsync(password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Cancellation));
    }

    private Task<HttpResponseMessage> ChangeAsync(JsonElement session, string currentPassword, string newPassword)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, Route)
        {
            Content = JsonContent.Create(new { CurrentPassword = currentPassword, NewPassword = newPassword }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.GetProperty("accessToken").GetString());
        return Client.SendAsync(request, Cancellation);
    }

    private Task<HttpResponseMessage> RefreshAsync(JsonElement session) =>
        Client.PostAsJsonAsync("/api/v1/identity/sessions/refresh", new { RefreshToken = session.GetProperty("refreshToken").GetString() }, Cancellation);
}
