using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Ouroboros.Modules.Identity.Application.Parameters;
using Ouroboros.Modules.Identity.Application.Security;
using Ouroboros.Modules.Identity.Domain.Users;
using Ouroboros.Modules.Identity.Infrastructure.Persistence;
using Ouroboros.Modules.Identity.Infrastructure.Security;

namespace Ouroboros.IntegrationTests;

// Bloqueio da conta após falhas seguidas de senha, contra o PostgreSQL real. O relógio é falso, para
// avançar o tempo do bloqueio, e o hasher usa poucas iterações, para os testes serem rápidos.
public sealed class AccountLockoutTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private const string Route = "/api/v1/identity/sessions";
    private const string Email = "cliente@example.com";
    private const string Password = "uma frase longa e secreta do cliente";
    private const string WrongPassword = "uma senha errada qualquer";

    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly RecordingPasswordHasher _hasher = new();
    private readonly ConcurrentQueue<string> _logs = new();

    [Fact]
    public async Task Five_failures_lock_the_account_even_for_the_correct_password()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        var userId = await CreateUserAsync(api);

        await FailAsync(client, times: 5);
        var response = await LoginAsync(client, Password);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(await ProblemTypeAsync(await LoginAsync(client, WrongPassword)), await ProblemTypeAsync(response));
        Assert.Equal(0, await QueryScalarAsync<int>($"SELECT failed_login_count FROM identity.users WHERE external_id = '{userId}'"));
        Assert.Equal(_time.GetUtcNow().AddMinutes(15).UtcDateTime, (await LockedUntilAsync())!.Value, TimeSpan.FromMilliseconds(1));
        Assert.Equal(0L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.sessions"));
    }

    [Fact]
    public async Task Locked_account_does_not_verify_the_real_password()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api);
        await FailAsync(client, times: 5);
        var realHash = await QueryScalarAsync<string>($"SELECT password_hash FROM identity.users WHERE email = '{Email}'");
        _hasher.VerifiedHashes.Clear();

        await LoginAsync(client, Password);

        var verified = Assert.Single(_hasher.VerifiedHashes);
        Assert.NotEqual(realHash, verified);
    }

    [Fact]
    public async Task Lock_expires_after_fifteen_minutes()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api);
        await FailAsync(client, times: 5);

        _time.Advance(TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, Password)).StatusCode);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, Password)).StatusCode);
        Assert.Null(await LockedUntilAsync());
    }

    [Fact]
    public async Task Successful_login_before_the_limit_resets_the_count()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api);

        await FailAsync(client, times: 4);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, Password)).StatusCode);
        await FailAsync(client, times: 1);

        Assert.Equal(1, await FailedLoginCountAsync());
        Assert.Null(await LockedUntilAsync());
    }

    [Fact]
    public async Task Concurrent_failures_are_all_counted()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api);
        await ChangeParameterAsync(api, "Identity:Lockout:MaxFailedAttempts", "99");

        await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => LoginAsync(client, WrongPassword)));

        Assert.Equal(30, await FailedLoginCountAsync());
    }

    [Fact]
    public async Task Limit_and_duration_come_from_the_system_parameters()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api);
        await ChangeParameterAsync(api, "Identity:Lockout:MaxFailedAttempts", "2");
        await ChangeParameterAsync(api, "Identity:Lockout:DurationMinutes", "1");

        await FailAsync(client, times: 2);

        Assert.Equal(_time.GetUtcNow().AddMinutes(1).UtcDateTime, (await LockedUntilAsync())!.Value, TimeSpan.FromMilliseconds(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, Password)).StatusCode);
    }

    // Sem a política de bloqueio, o login falha igual para qualquer e-mail, em vez de liberar tentativas
    // ilimitadas ou revelar quais contas existem.
    [Fact]
    public async Task Missing_lockout_parameter_fails_the_same_way_for_any_email()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        await CreateUserAsync(api);
        await ExecuteAsync("DELETE FROM identity.parameters WHERE key = 'Identity:Lockout:MaxFailedAttempts'");

        var existing = await LoginAsync(client, Password);
        var unknown = await client.PostAsJsonAsync(Route, new { Email = "ninguem@example.com", Password }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, existing.StatusCode);
        Assert.Equal(existing.StatusCode, unknown.StatusCode);
        Assert.Equal(0L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.sessions"));
    }

    [Fact]
    public async Task Lock_is_logged_with_the_external_id_only()
    {
        await using var api = CreateApi();
        using var client = api.CreateClient();
        var userId = await CreateUserAsync(api);

        await FailAsync(client, times: 5);

        var entry = Assert.Single(_logs, entry => entry.Contains("bloqueada", StringComparison.Ordinal));
        Assert.Contains(userId.ToString(), entry);
        Assert.DoesNotContain(_logs, log => log.Contains(Email, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(_logs, log => log.Contains(WrongPassword, StringComparison.Ordinal) || log.Contains(Password, StringComparison.Ordinal));
    }

    private WebApplicationFactory<Program> CreateApi() => Api.WithWebHostBuilder(builder =>
    {
        builder.ConfigureLogging(logging => logging.AddProvider(new ListLoggerProvider(_logs)));
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(_time);
            services.AddSingleton<IPasswordHasher>(_hasher);
        });
    });

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string password) =>
        client.PostAsJsonAsync(Route, new { Email, Password = password }, TestContext.Current.CancellationToken);

    private static async Task FailAsync(HttpClient client, int times)
    {
        for (var attempt = 0; attempt < times; attempt++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, WrongPassword)).StatusCode);
        }
    }

    private static async Task<string?> ProblemTypeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<Dictionary<string, object>>(TestContext.Current.CancellationToken))?["type"]?.ToString();

    private static async Task<Guid> CreateUserAsync(WebApplicationFactory<Program> api)
    {
        await using var scope = api.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var user = User.Create(Email, scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(Password), UserRole.Customer);
        context.Users.Add(user);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return user.ExternalId;
    }

    private static async Task ChangeParameterAsync(WebApplicationFactory<Program> api, string key, string value)
    {
        await using var scope = api.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ParameterWriter>()
            .ChangeAsync(key, value, null, Guid.CreateVersion7(), TestContext.Current.CancellationToken);
    }

    private Task<int> FailedLoginCountAsync() =>
        QueryScalarAsync<int>($"SELECT failed_login_count FROM identity.users WHERE email = '{Email}'");

    // O Npgsql lê timestamptz como DateTime em UTC.
    private Task<DateTime?> LockedUntilAsync() =>
        QueryScalarAsync<DateTime?>($"SELECT locked_until FROM identity.users WHERE email = '{Email}'");

    // Registra o hash contra o qual cada verificação foi feita.
    private sealed class RecordingPasswordHasher : IPasswordHasher
    {
        private readonly AspNetCorePasswordHasher _inner = new(iterationCount: 1_000);

        public ConcurrentQueue<string> VerifiedHashes { get; } = new();

        public string Hash(string password) => _inner.Hash(password);

        public PasswordVerificationOutcome Verify(string passwordHash, string providedPassword)
        {
            VerifiedHashes.Enqueue(passwordHash);
            return _inner.Verify(passwordHash, providedPassword);
        }
    }
}
