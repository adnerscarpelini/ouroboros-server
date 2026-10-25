using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Npgsql;
using Ouroboros.Identity.Application.Security;
using Ouroboros.Identity.Domain.Users;
using Ouroboros.Identity.Infrastructure.Persistence;
using Ouroboros.Identity.Infrastructure.Security;

namespace Ouroboros.IntegrationTests;

// Revogação da sessão ao detectar o reuso de um refresh token já revogado, contra o PostgreSQL real.
public sealed class RefreshTokenReuseTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private const string LoginRoute = "/api/v1/identity/sessions";
    private const string RefreshRoute = "/api/v1/identity/sessions/refresh";
    private const string Email = "cliente@example.com";
    private const string Password = "uma frase longa e secreta do cliente";
    private const string InvalidRefreshTokenType =
        "https://github.com/adnerscarpelini/ouroboros-server/blob/main/docs/errors/identity.md#invalid-refresh-token";

    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly ConcurrentQueue<string> _logs = new();

    [Fact]
    public async Task Reused_token_revokes_its_successor()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api);
        var first = RefreshTokenOf(await LoginAsync(client));
        var successor = RefreshTokenOf(await RenewAsync(client, first));

        await AssertInvalidRefreshTokenAsync(await RefreshAsync(client, first));

        await AssertInvalidRefreshTokenAsync(await RefreshAsync(client, successor));
        Assert.Equal(0L, await ActiveTokensAsync());
    }

    [Fact]
    public async Task Reuse_in_one_session_does_not_affect_the_others()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api);
        var stolen = RefreshTokenOf(await LoginAsync(client));
        var other = await LoginAsync(client);
        await RenewAsync(client, stolen);

        await AssertInvalidRefreshTokenAsync(await RefreshAsync(client, stolen));

        var renewed = await RenewAsync(client, RefreshTokenOf(other));
        Assert.Equal(SessionIdOf(other), SessionIdOf(renewed));
        Assert.Equal(1L, await ActiveTokensAsync());
    }

    [Fact]
    public async Task Concurrent_renewals_with_the_same_token_revoke_the_session()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api);
        var token = RefreshTokenOf(await LoginAsync(client));

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => RefreshAsync(client, token)));

        var winner = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.All(responses.Where(response => response != winner), response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
        var winnerToken = RefreshTokenOf(await winner.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken));
        Assert.Equal(0L, await ActiveTokensAsync());
        await AssertInvalidRefreshTokenAsync(await RefreshAsync(client, winnerToken));
    }

    // Uma renovação em andamento (simulada numa conexão própria, com os mesmos passos e locks do caso de
    // uso) grava um token novo; o reuso apresentado ao mesmo tempo precisa revogar também esse token.
    [Fact]
    public async Task Reuse_during_an_in_flight_renewal_also_revokes_its_new_token()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api);
        var first = RefreshTokenOf(await LoginAsync(client));
        await RenewAsync(client, first);

        await using var renewal = new NpgsqlConnection(ConnectionString);
        await renewal.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await renewal.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var command = new NpgsqlCommand("""
            SELECT 1 FROM identity.sessions FOR UPDATE;
            UPDATE identity.refresh_tokens SET revoked_at = now(), updated_at = now() WHERE revoked_at IS NULL;
            INSERT INTO identity.refresh_tokens (external_id, session_id, token_hash, expires_at, created_at, updated_at)
            SELECT uuidv7(), id, sha256(random()::text::bytea), now() + interval '7 days', now(), now() FROM identity.sessions;
            """, renewal, transaction))
        {
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var reuse = RefreshAsync(client, first);
        await WaitForLockWaitAsync();
        await transaction.CommitAsync(TestContext.Current.CancellationToken);

        await AssertInvalidRefreshTokenAsync(await reuse);
        Assert.Equal(0L, await ActiveTokensAsync());
    }

    // O atacante encadeia renovações enquanto o cliente legítimo reapresenta o token antigo: a sessão
    // termina sem nenhum token ativo em todas as rodadas.
    [Fact]
    public async Task Reuse_racing_a_chain_of_renewals_leaves_no_active_token()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api);

        for (var round = 0; round < 10; round++)
        {
            var first = RefreshTokenOf(await LoginAsync(client));
            var successor = RefreshTokenOf(await RenewAsync(client, first));

            var chain = Task.Run(async () =>
            {
                var current = successor;
                for (var step = 0; step < 20; step++)
                {
                    var response = await RefreshAsync(client, current);
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        return;
                    }

                    current = RefreshTokenOf(await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken));
                }
            }, TestContext.Current.CancellationToken);
            await Task.Delay(round * 3, TestContext.Current.CancellationToken);
            await RefreshAsync(client, first);
            await chain;

            Assert.Equal(0L, await ActiveTokensAsync());
        }
    }

    [Fact]
    public async Task Expired_token_does_not_revoke_the_session()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api);
        var expired = RefreshTokenOf(await LoginAsync(client));
        _time.Advance(TimeSpan.FromDays(7));
        var current = await LoginAsync(client);

        await AssertInvalidRefreshTokenAsync(await RefreshAsync(client, expired));

        Assert.Equal(0L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.refresh_tokens WHERE revoked_at IS NOT NULL"));
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(client, RefreshTokenOf(current))).StatusCode);
    }

    // O logout (#17) revoga o token; aqui a revogação é feita direto no banco.
    [Fact]
    public async Task Token_revoked_by_logout_returns_401()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api);
        var token = RefreshTokenOf(await LoginAsync(client));
        await ExecuteAsync("UPDATE identity.refresh_tokens SET revoked_at = now()");

        await AssertInvalidRefreshTokenAsync(await RefreshAsync(client, token));
    }

    [Fact]
    public async Task Reuse_is_logged_with_user_and_session_ids_but_never_the_token()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        var userId = await CreateUserAsync(api);
        var login = await LoginAsync(client);
        var first = RefreshTokenOf(login);
        var successor = RefreshTokenOf(await RenewAsync(client, first));

        await RefreshAsync(client, first);

        var entry = Assert.Single(_logs, log => log.Contains("Reuso", StringComparison.Ordinal));
        Assert.Contains(userId.ToString(), entry);
        Assert.Contains(SessionIdOf(login), entry);
        Assert.DoesNotContain(_logs, log => log.Contains(first, StringComparison.Ordinal) || log.Contains(successor, StringComparison.Ordinal));
    }

    private WebApplicationFactory<Program> CreateApi() => Api.WithWebHostBuilder(builder =>
    {
        builder.ConfigureLogging(logging => logging.AddProvider(new ListLoggerProvider(_logs)));
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(_time);
            services.AddSingleton<IPasswordHasher>(new AspNetCorePasswordHasher(iterationCount: 1_000));
        });
    });

    private static async Task<Guid> CreateUserAsync(WebApplicationFactory<Program> api)
    {
        await using var scope = api.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var user = User.Create(Email, scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(Password), UserRole.Customer);
        context.Users.Add(user);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return user.ExternalId;
    }

    private static async Task<JsonElement> LoginAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(LoginRoute, new { Email, Password }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }

    private static async Task<JsonElement> RenewAsync(HttpClient client, string refreshToken)
    {
        var response = await RefreshAsync(client, refreshToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }

    private static Task<HttpResponseMessage> RefreshAsync(HttpClient client, string refreshToken) =>
        client.PostAsJsonAsync(RefreshRoute, new { RefreshToken = refreshToken }, TestContext.Current.CancellationToken);

    private static string RefreshTokenOf(JsonElement body) => body.GetProperty("refreshToken").GetString()!;

    private static string SessionIdOf(JsonElement body) =>
        new JsonWebToken(body.GetProperty("accessToken").GetString()).GetClaim("sid").Value;

    private static async Task AssertInvalidRefreshTokenAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(InvalidRefreshTokenType, body.GetProperty("type").GetString());
    }

    // Espera a requisição de reuso ficar bloqueada no lock da sessão.
    private async Task WaitForLockWaitAsync()
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (await QueryScalarAsync<long>("SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'") > 0)
            {
                return;
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.Fail("A requisição de reuso não ficou esperando o lock da sessão.");
    }

    private Task<long> ActiveTokensAsync() =>
        QueryScalarAsync<long>("SELECT count(*) FROM identity.refresh_tokens WHERE revoked_at IS NULL");
}
