using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ouroboros.Identity.Application.Notifications;
using Ouroboros.Identity.Application.Security;
using Ouroboros.Identity.Domain.Users;
using Ouroboros.Identity.Infrastructure.Persistence;
using Ouroboros.Identity.Infrastructure.Security;

namespace Ouroboros.IntegrationTests;

// Corridas entre a redefinição de senha e o login, com a ordem forçada por um hasher que pausa no meio
// do fluxo. A redefinição e a abertura de sessão são serializadas pela linha do usuário.
public sealed class PasswordResetRaceTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private const string Email = "cliente@example.com";
    private const string OldPassword = "uma frase longa e antiga do usuario";
    private const string NewPassword = "outra frase longa e bem diferente agora";
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(30);

    private readonly PausingPasswordHasher _hasher = new();

    // Login com a senha antiga conferida antes da redefinição, mas que abriria a sessão depois dela.
    [Fact]
    public async Task Login_verified_before_the_reset_does_not_keep_a_session()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api);
        var token = await RequestTokenAsync(client);

        _hasher.PauseOnVerifyOf = OldPassword;
        var login = LoginAsync(client, OldPassword);
        Assert.True(_hasher.Reached.Wait(s_timeout, TestContext.Current.CancellationToken));
        var reset = await CompleteAsync(client, token, NewPassword);
        _hasher.Release.Set();

        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await login).StatusCode);
        Assert.Equal(0L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.refresh_tokens WHERE revoked_at IS NULL"));
    }

    // Login que refaria o hash da senha antiga não grava esse hash por cima da senha nova.
    [Fact]
    public async Task Rehash_during_the_reset_does_not_bring_the_old_password_back()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api);
        var token = await RequestTokenAsync(client);

        _hasher.RehashFor = OldPassword;
        _hasher.PauseOnVerifyOf = OldPassword;
        var login = LoginAsync(client, OldPassword);
        Assert.True(_hasher.Reached.Wait(s_timeout, TestContext.Current.CancellationToken));
        var reset = await CompleteAsync(client, token, NewPassword);
        _hasher.Release.Set();

        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await login).StatusCode);
        _hasher.PauseOnVerifyOf = null;
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, OldPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, NewPassword)).StatusCode);
    }

    // Bloqueio atingido enquanto a redefinição calcula o hash também é removido por ela.
    [Fact]
    public async Task Lock_reached_during_the_reset_is_removed_by_it()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api);
        var token = await RequestTokenAsync(client);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            await LoginAsync(client, "uma senha errada qualquer");
        }

        _hasher.PauseOnHashOf = NewPassword;
        var reset = CompleteAsync(client, token, NewPassword);
        Assert.True(_hasher.Reached.Wait(s_timeout, TestContext.Current.CancellationToken));
        await LoginAsync(client, "uma senha errada qualquer");
        Assert.NotNull(await QueryScalarAsync<DateTime?>($"SELECT locked_until FROM identity.users WHERE email = '{Email}'"));
        _hasher.Release.Set();

        Assert.Equal(HttpStatusCode.NoContent, (await reset).StatusCode);
        Assert.Null(await QueryScalarAsync<DateTime?>($"SELECT locked_until FROM identity.users WHERE email = '{Email}'"));
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, NewPassword)).StatusCode);
    }

    // Com o usuário travado por uma redefinição ou um login em andamento, uma nova solicitação de
    // redefinição (que insere um token com FK para o usuário) não espera nem entra em deadlock.
    [Fact]
    public async Task Request_while_the_user_is_locked_does_not_wait()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api);
        await using var scope = api.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var userId = await context.Users.Where(user => user.Email == Email).Select(user => user.Id).SingleAsync(TestContext.Current.CancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await context.LockUserAsync(userId, TestContext.Current.CancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var response = await client.PostAsJsonAsync("/api/v1/identity/password-resets", new { Email }, timeout.Token);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Single((await TestOutbox.ReadAsync(Api.Services)));
        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
    }

    private ApiFactory CreateApi() => Api.WithWebHostBuilder(builder =>
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IPasswordHasher>(_hasher);
        }));

    private static async Task CreateUserAsync(ApiFactory api)
    {
        await using var scope = api.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        context.Users.Add(User.Create(Email, scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(OldPassword), UserRole.Customer));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<string> RequestTokenAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/v1/identity/password-resets", new { Email }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return await TestOutbox.TokenAsync(Api.Services, Email);
    }

    private static Task<HttpResponseMessage> CompleteAsync(HttpClient client, string token, string password) =>
        client.PostAsJsonAsync("/api/v1/identity/password-resets/complete", new { Token = token, Password = password }, TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string password) =>
        Task.Run(() => client.PostAsJsonAsync("/api/v1/identity/sessions", new { Email, Password = password }, TestContext.Current.CancellationToken));

    // Hasher rápido que pode pausar uma única vez numa verificação ou num hash de uma senha escolhida, e
    // simular que o hash armazenado precisa ser refeito.
    private sealed class PausingPasswordHasher : IPasswordHasher
    {
        private readonly AspNetCorePasswordHasher _inner = new(iterationCount: 1_000);
        private int _paused;

        public string? PauseOnVerifyOf { get; set; }

        public string? PauseOnHashOf { get; set; }

        public string? RehashFor { get; set; }

        public ManualResetEventSlim Reached { get; } = new();

        public ManualResetEventSlim Release { get; } = new();

        public string Hash(string password)
        {
            PauseIf(password == PauseOnHashOf);
            return _inner.Hash(password);
        }

        public PasswordVerificationOutcome Verify(string passwordHash, string providedPassword)
        {
            var outcome = _inner.Verify(passwordHash, providedPassword);
            if (outcome == PasswordVerificationOutcome.Succeeded && providedPassword == RehashFor)
            {
                outcome = PasswordVerificationOutcome.SucceededRehashNeeded;
            }

            PauseIf(outcome != PasswordVerificationOutcome.Failed && providedPassword == PauseOnVerifyOf);
            return outcome;
        }

        private void PauseIf(bool condition)
        {
            if (condition && Interlocked.Exchange(ref _paused, 1) == 0)
            {
                Reached.Set();
                Release.Wait(s_timeout);
            }
        }
    }
}
