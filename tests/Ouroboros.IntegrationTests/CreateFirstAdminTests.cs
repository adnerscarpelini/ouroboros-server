extern alias backoffice;

using System.Collections.Concurrent;
using backoffice::Ouroboros.Backoffice.Api.CommandLine;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ouroboros.Modules.Identity.Contracts.Administrators;
using Ouroboros.Modules.Identity.Domain.Users;
using Ouroboros.Modules.Identity.Infrastructure.Persistence;

namespace Ouroboros.IntegrationTests;

// Criação do primeiro administrador (caso de uso e comando create-admin) contra o PostgreSQL real.
public sealed class CreateFirstAdminTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private const string Email = "oi@adnerscarpelini.com";
    private const string Password = "uma frase longa e única para o admin";

    [Fact]
    public async Task Command_creates_active_admin_and_exits_with_zero()
    {
        var (exitCode, output, error) = await RunCommandAsync(["create-admin", "--email", "  OI@AdnerScarpelini.com "], Password);

        Assert.Equal(CreateAdminCommand.Success, exitCode);
        Assert.Contains(Email, output);
        Assert.Empty(error);
        Assert.Equal("Admin", await QueryScalarAsync<string>($"SELECT role FROM identity.users WHERE email = '{Email}'"));
        Assert.True(await QueryScalarAsync<bool>($"SELECT is_active FROM identity.users WHERE email = '{Email}'"));
    }

    [Fact]
    public async Task Created_admin_password_verifies_with_the_hasher()
    {
        await RunCommandAsync(["create-admin", "--email", Email], Password);

        await using var scope = Api.Services.CreateAsyncScope();
        var admin = await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Users
            .SingleAsync(user => user.Email == Email, TestContext.Current.CancellationToken);
        var verifier = scope.ServiceProvider.GetRequiredService<Ouroboros.Modules.Identity.Application.Security.UserPasswordVerifier>();
        Assert.True(verifier.Verify(admin, Password));
        Assert.DoesNotContain(Password, admin.PasswordHash);
    }

    [Fact]
    public async Task Second_admin_is_not_created()
    {
        await RunCommandAsync(["create-admin", "--email", Email], Password);

        var (exitCode, _, error) = await RunCommandAsync(["create-admin", "--email", "outro@adnerscarpelini.com"], Password);

        Assert.Equal(CreateAdminCommand.Failure, exitCode);
        Assert.Contains("Já existe um administrador", error);
        Assert.Equal(1L, await CountUsersAsync());
    }

    [Fact]
    public async Task Email_already_used_by_another_user_creates_nothing()
    {
        await using (var scope = Api.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            context.Users.Add(User.Create(Email, "hash", UserRole.Customer));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var result = await ExecuteAsync(Email, Password);

        Assert.Equal(CreateFirstAdminOutcome.EmailAlreadyInUse, result.Outcome);
        Assert.Equal(1L, await CountUsersAsync());
        Assert.Equal("Customer", await QueryScalarAsync<string>($"SELECT role FROM identity.users WHERE email = '{Email}'"));
    }

    [Theory]
    [InlineData("oi@outrodominio.com")]
    [InlineData("oi@rh.adnerscarpelini.com")]
    public async Task Email_outside_allowed_domain_creates_nothing(string email)
    {
        var (exitCode, _, error) = await RunCommandAsync(["create-admin", "--email", email], Password);

        Assert.Equal(CreateAdminCommand.Failure, exitCode);
        Assert.Contains("adnerscarpelini.com", error);
        Assert.Equal(0L, await CountUsersAsync());
    }

    [Fact]
    public async Task Missing_domain_parameter_is_reported_without_exception()
    {
        await ExecuteAsync(sql: "DELETE FROM settings.parameters WHERE key = 'Identity:AdminInvitations:AllowedEmailDomain'");

        var result = await ExecuteAsync(Email, Password);

        Assert.Equal(CreateFirstAdminOutcome.ConfigurationMissing, result.Outcome);
        Assert.Contains("Identity:AdminInvitations:AllowedEmailDomain", result.Message);
        Assert.Equal(0L, await CountUsersAsync());
    }

    [Fact]
    public async Task Short_password_creates_nothing_and_informs_minimum_length()
    {
        var (exitCode, _, error) = await RunCommandAsync(["create-admin", "--email", Email], "curta14chars!!");

        Assert.Equal(CreateAdminCommand.Failure, exitCode);
        Assert.Contains("15", error);
        Assert.Equal(0L, await CountUsersAsync());
    }

    [Fact]
    public async Task Password_above_limit_is_rejected_not_truncated()
    {
        var result = await ExecuteAsync(Email, new string('x', 120) + "abcdefghi");

        Assert.Equal(CreateFirstAdminOutcome.PasswordRejected, result.Outcome);
        Assert.Equal(0L, await CountUsersAsync());
    }

    [Fact]
    public async Task Common_password_in_any_case_is_rejected()
    {
        var result = await ExecuteAsync(Email, "PassWordPassWord");

        Assert.Equal(CreateFirstAdminOutcome.PasswordRejected, result.Outcome);
        Assert.Equal(0L, await CountUsersAsync());
    }

    [Fact]
    public async Task Missing_password_variable_creates_nothing()
    {
        var (exitCode, _, error) = await RunCommandAsync(["create-admin", "--email", Email], password: null);

        Assert.Equal(CreateAdminCommand.UsageError, exitCode);
        Assert.Contains(CreateAdminCommand.PasswordVariable, error);
        Assert.Equal(0L, await CountUsersAsync());
    }

    [Fact]
    public async Task Password_never_appears_in_output_or_logs()
    {
        var logs = new ConcurrentQueue<string>();
        await using var api = Api.WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging => logging.AddProvider(new ListLoggerProvider(logs))));

        var outputs = new List<string>();
        foreach (var password in new[] { Password, "curta", "PassWordPassWord", Password })
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            await CreateAdminCommand.RunAsync(
                api.Services, ["create-admin", "--email", Email], _ => password, output, error, TestContext.Current.CancellationToken);
            outputs.Add(output.ToString() + error);
        }

        Assert.All(outputs, text => Assert.DoesNotContain(Password, text));
        Assert.DoesNotContain(logs, entry => entry.Contains(Password, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Concurrent_runs_create_only_one_admin()
    {
        var results = await Task.WhenAll(
            ExecuteAsync("oi@adnerscarpelini.com", Password),
            ExecuteAsync("outro@adnerscarpelini.com", Password));

        Assert.Single(results, result => result.Succeeded);
        Assert.Single(results, result => result.Outcome == CreateFirstAdminOutcome.AdminAlreadyExists);
        Assert.Equal(1L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.users WHERE role = 'Admin'"));
    }

    private async Task<CreateFirstAdminResult> ExecuteAsync(string email, string password)
    {
        await using var scope = Api.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICreateFirstAdmin>()
            .ExecuteAsync(new CreateFirstAdminRequest(email, password), TestContext.Current.CancellationToken);
    }

    private async Task<(int ExitCode, string Output, string Error)> RunCommandAsync(string[] args, string? password)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await CreateAdminCommand.RunAsync(
            Api.Services,
            args,
            name => name == CreateAdminCommand.PasswordVariable ? password : null,
            output,
            error,
            TestContext.Current.CancellationToken);
        return (exitCode, output.ToString(), error.ToString());
    }

    private Task<long> CountUsersAsync() => QueryScalarAsync<long>("SELECT count(*) FROM identity.users");

    private sealed class ListLoggerProvider(ConcurrentQueue<string> entries) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ListLogger(entries);

        public void Dispose()
        {
        }

        private sealed class ListLogger(ConcurrentQueue<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(formatter(state, exception) + exception);
        }
    }
}
