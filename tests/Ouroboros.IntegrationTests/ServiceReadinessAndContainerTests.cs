extern alias identityapi;

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using identityapi::Ouroboros.Identity.Api.CommandLine;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Ouroboros.Hosting;
using Ouroboros.Identity.Contracts.Administrators;
using Ouroboros.Identity.Infrastructure.Persistence;
using Ouroboros.Notifications;
using Ouroboros.Notifications.Infrastructure.Persistence;

namespace Ouroboros.IntegrationTests;

[Collection("Telemetry")]
public sealed class ServiceReadinessAndContainerTests(RabbitMqFixture broker, ProvisionedInstance database)
    : IClassFixture<RabbitMqFixture>, IClassFixture<ProvisionedInstance>
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Services_do_not_apply_migrations_on_startup_and_become_ready_only_after_explicit_migration_with_own_credentials()
    {
        await database.ExecuteAdminAsync(ProvisionedInstance.IdentityDatabase, "DROP SCHEMA IF EXISTS identity CASCADE");
        await database.ExecuteAdminAsync(ProvisionedInstance.NotificationsDatabase, "DROP SCHEMA IF EXISTS notifications CASCADE");
        Assert.Equal(0, (await database.ProvisionAsync()).ExitCode);

        var identityRuntimeCs = database.ConnectionStringFor(ProvisionedInstance.IdentityRuntime, ProvisionedInstance.IdentityDatabase);
        var identityMigratorCs = database.ConnectionStringFor(ProvisionedInstance.IdentityMigrator, ProvisionedInstance.IdentityDatabase);
        var notificationsRuntimeCs = database.ConnectionStringFor(ProvisionedInstance.NotificationsRuntime, ProvisionedInstance.NotificationsDatabase);
        var notificationsMigratorCs = database.ConnectionStringFor(ProvisionedInstance.NotificationsMigrator, ProvisionedInstance.NotificationsDatabase);

        // Runtime roles são recusadas pelo comando explícito de migration.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Ouroboros.Identity.DependencyInjection.MigrateDatabaseAsync(identityRuntimeCs, Cancellation));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Ouroboros.Notifications.DependencyInjection.MigrateDatabaseAsync(notificationsRuntimeCs, Cancellation));

        // Antes das migrations explícitas: subir Identity e Notifications não cria schemas/tabelas e readiness reporta 503.
        await using var api = new IntegrationApiFactory(identityRuntimeCs)
            .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Identity"] = identityRuntimeCs,
                })));
        using var identityClient = api.CreateClient();

        using (var liveBefore = await identityClient.GetAsync(OuroborosHealthExtensions.LivePath, Cancellation))
        {
            Assert.Equal(HttpStatusCode.OK, liveBefore.StatusCode);
        }

        using (var readyBefore = await identityClient.GetAsync(OuroborosHealthExtensions.ReadyPath, Cancellation))
        {
            var body = await readyBefore.Content.ReadAsStringAsync(Cancellation);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, readyBefore.StatusCode);
            Assert.Equal("{\"status\":\"Unhealthy\"}", body);
            Assert.DoesNotContain(database.Passwords[ProvisionedInstance.IdentityRuntime], body, StringComparison.Ordinal);
        }

        using var keys = new NotificationTestKeys();
        var workerHealthPort = FreeTcpPort();
        using var unmigratedWorker = StartWorkerWithHealthPort(keys, notificationsRuntimeCs, workerHealthPort);
        using var workerClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{workerHealthPort}") };
        try
        {
            await WaitForHttpStatusAsync(workerClient, OuroborosHealthExtensions.LivePath, HttpStatusCode.OK);
            using var readyBefore = await workerClient.GetAsync(OuroborosHealthExtensions.ReadyPath, Cancellation);
            var body = await readyBefore.Content.ReadAsStringAsync(Cancellation);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, readyBefore.StatusCode);
            Assert.Equal("{\"status\":\"Unhealthy\"}", body);
            Assert.DoesNotContain(database.Passwords[ProvisionedInstance.NotificationsRuntime], body, StringComparison.Ordinal);
            Assert.DoesNotContain(broker.ConsumerPassword, body, StringComparison.Ordinal);
        }
        finally
        {
            await StopProcessAsync(unmigratedWorker);
        }

        // Executa migrations explicitamente com as roles de migration e converge os privilégios de runtime.
        using (var outWriter = new StringWriter())
        using (var errWriter = new StringWriter())
        {
            var exitCode = await MigrateDatabaseCommand.RunAsync(
                key => key == "ConnectionStrings__Identity" ? identityMigratorCs : null,
                outWriter,
                errWriter,
                Cancellation);
            Assert.Equal(MigrateDatabaseCommand.Success, exitCode);
            Assert.DoesNotContain(database.Passwords[ProvisionedInstance.IdentityMigrator], outWriter.ToString() + errWriter.ToString(), StringComparison.Ordinal);
        }

        await Ouroboros.Notifications.DependencyInjection.MigrateDatabaseAsync(notificationsMigratorCs, Cancellation);
        Assert.Equal(0, (await database.ProvisionAsync()).ExitCode);

        // Após migrations e convergência de privilégios: ambos os serviços ficam prontos (200 OK) com suas credenciais de runtime.
        using (var readyAfter = await identityClient.GetAsync(OuroborosHealthExtensions.ReadyPath, Cancellation))
        {
            Assert.Equal(HttpStatusCode.OK, readyAfter.StatusCode);
            Assert.Equal("{\"status\":\"Healthy\"}", await readyAfter.Content.ReadAsStringAsync(Cancellation));
        }

        var migratedPort = FreeTcpPort();
        using var migratedWorker = StartWorkerWithHealthPort(keys, notificationsRuntimeCs, migratedPort);
        using var migratedClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{migratedPort}") };
        try
        {
            await WaitForHttpStatusAsync(migratedClient, OuroborosHealthExtensions.ReadyPath, HttpStatusCode.OK);
            using var readyResponse = await migratedClient.GetAsync(OuroborosHealthExtensions.ReadyPath, Cancellation);
            Assert.Equal(HttpStatusCode.OK, readyResponse.StatusCode);
            Assert.Equal("{\"status\":\"Healthy\"}", await readyResponse.Content.ReadAsStringAsync(Cancellation));
        }
        finally
        {
            await StopProcessAsync(migratedWorker);
        }
    }

    [Fact]
    public async Task Stopped_broker_keeps_identity_ready_and_login_working_while_notifications_reports_not_ready()
    {
        var identityMigratorCs = database.ConnectionStringFor(ProvisionedInstance.IdentityMigrator, ProvisionedInstance.IdentityDatabase);
        var identityRuntimeCs = database.ConnectionStringFor(ProvisionedInstance.IdentityRuntime, ProvisionedInstance.IdentityDatabase);
        var notificationsMigratorCs = database.ConnectionStringFor(ProvisionedInstance.NotificationsMigrator, ProvisionedInstance.NotificationsDatabase);
        var notificationsRuntimeCs = database.ConnectionStringFor(ProvisionedInstance.NotificationsRuntime, ProvisionedInstance.NotificationsDatabase);

        await Ouroboros.Identity.DependencyInjection.MigrateDatabaseAsync(identityMigratorCs, Cancellation);
        await Ouroboros.Notifications.DependencyInjection.MigrateDatabaseAsync(notificationsMigratorCs, Cancellation);
        Assert.Equal(0, (await database.ProvisionAsync()).ExitCode);

        await using var api = new IntegrationApiFactory(identityRuntimeCs)
            .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Identity"] = identityRuntimeCs,
                    ["Identity:EmailPublisher:Enabled"] = "true",
                    ["Identity:EmailPublisher:Host"] = broker.Host,
                    ["Identity:EmailPublisher:Port"] = broker.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["Identity:EmailPublisher:User"] = "identity_publisher",
                    ["Identity:EmailPublisher:Password"] = broker.IdentityPassword,
                })));

        const string adminEmail = "broker-down-admin@adnerscarpelini.com";
        const string adminPassword = "uma frase longa e segura para o admin do teste";
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            await context.Users.Where(user => user.Email == adminEmail).ExecuteDeleteAsync(Cancellation);
            var createAdmin = scope.ServiceProvider.GetRequiredService<ICreateFirstAdmin>();
            var created = await createAdmin.ExecuteAsync(new CreateFirstAdminRequest(adminEmail, adminPassword), Cancellation);
            if (!created.Succeeded)
            {
                // Caso já exista um admin na fixture compartilhada, garante credencial conhecida de login no listener administrativo.
                await context.Sessions.ExecuteDeleteAsync(Cancellation);
                await context.Users.ExecuteDeleteAsync(Cancellation);
                Assert.True((await createAdmin.ExecuteAsync(new CreateFirstAdminRequest(adminEmail, adminPassword), Cancellation)).Succeeded);
            }
        }

        using var keys = new NotificationTestKeys();
        var workerPort = FreeTcpPort();
        using var worker = StartWorkerWithHealthPort(keys, notificationsRuntimeCs, workerPort);
        using var workerHttp = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{workerPort}") };
        using var publicClient = api.CreateClient();
        using var adminClient = api.CreateClient();
        adminClient.DefaultRequestHeaders.Add(IntegrationApiFactory.ListenerHeader, IntegrationApiFactory.AdminListener);

        try
        {
            await WaitForHttpStatusAsync(workerHttp, OuroborosHealthExtensions.ReadyPath, HttpStatusCode.OK);

            // Para o broker RabbitMQ.
            await broker.StopAsync();
            try
            {
                // Identity continua pronto nos dois listeners e o login funciona normalmente.
                using var identityReady = await publicClient.GetAsync(OuroborosHealthExtensions.ReadyPath, Cancellation);
                Assert.Equal(HttpStatusCode.OK, identityReady.StatusCode);
                Assert.Equal("{\"status\":\"Healthy\"}", await identityReady.Content.ReadAsStringAsync(Cancellation));

                using var adminReady = await adminClient.GetAsync(OuroborosHealthExtensions.ReadyPath, Cancellation);
                Assert.Equal(HttpStatusCode.OK, adminReady.StatusCode);

                using var loginResponse = await adminClient.PostAsJsonAsync(
                    "/api/v1/admin/identity/sessions",
                    new { Email = adminEmail, Password = adminPassword },
                    Cancellation);
                Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

                // Notifications continua vivo (liveness 200), mas reporta não pronto (readiness 503) sem vazar segredos.
                using var workerLive = await workerHttp.GetAsync(OuroborosHealthExtensions.LivePath, Cancellation);
                Assert.Equal(HttpStatusCode.OK, workerLive.StatusCode);

                using var workerReady = await workerHttp.GetAsync(OuroborosHealthExtensions.ReadyPath, Cancellation);
                var workerBody = await workerReady.Content.ReadAsStringAsync(Cancellation);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, workerReady.StatusCode);
                Assert.Equal("{\"status\":\"Unhealthy\"}", workerBody);
                Assert.DoesNotContain(broker.ConsumerPassword, workerBody, StringComparison.Ordinal);
                Assert.DoesNotContain(database.Passwords[ProvisionedInstance.NotificationsRuntime], workerBody, StringComparison.Ordinal);
            }
            finally
            {
                await broker.StartAsync();
                await broker.ProvisionAsync();
            }

            // Ao retornar o broker, Notifications volta a reportar pronto.
            await WaitForHttpStatusAsync(workerHttp, OuroborosHealthExtensions.ReadyPath, HttpStatusCode.OK);
        }
        finally
        {
            await StopProcessAsync(worker);
        }
    }

    [Fact]
    public async Task Versioned_docker_images_run_as_non_root_and_carry_version_and_commit_without_secrets()
    {
        const string version = "0.1.0";
        const string commit = "98abc123def0";
        const string tag = $"{version}-{commit}";
        const string identityImage = $"ouroboros-identity-api-test:{tag}";
        const string notificationsImage = $"ouroboros-notifications-worker-test:{tag}";

        await RunDockerAsync(
            "build", "-f", "src/Services/Identity/Ouroboros.Identity.Api/Dockerfile",
            "--build-arg", $"APP_VERSION={version}",
            "--build-arg", $"SOURCE_REVISION={commit}",
            "-t", identityImage, ".");

        await RunDockerAsync(
            "build", "-f", "src/Services/Notifications/Ouroboros.Notifications.Worker/Dockerfile",
            "--build-arg", $"APP_VERSION={version}",
            "--build-arg", $"SOURCE_REVISION={commit}",
            "-t", notificationsImage, ".");

        try
        {
            var identityCheck = await RunDockerAsync(
                "run", "--rm", "--entrypoint", "sh", identityImage, "-c",
                "id -u; ls /app/*.dll; test ! -f /app/.env");
            Assert.Equal("1654", identityCheck.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim());
            Assert.DoesNotContain("Ouroboros.Notifications", identityCheck, StringComparison.Ordinal);

            var notificationsCheck = await RunDockerAsync(
                "run", "--rm", "--entrypoint", "sh", notificationsImage, "-c",
                "id -u; test -w /app/.local/notifications-emails; ls /app/*.dll; test ! -f /app/.env");
            Assert.Equal("1654", notificationsCheck.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim());
            Assert.DoesNotContain("Ouroboros.Identity", notificationsCheck, StringComparison.Ordinal);

            var identityLabels = await RunDockerAsync(
                "image", "inspect", "--format", "{{index .Config.Labels \"org.opencontainers.image.version\"}}", identityImage);
            var notificationsLabels = await RunDockerAsync(
                "image", "inspect", "--format", "{{index .Config.Labels \"org.opencontainers.image.version\"}}", notificationsImage);
            Assert.Equal(tag, identityLabels.Trim());
            Assert.Equal(tag, notificationsLabels.Trim());
        }
        finally
        {
            await TryRemoveImageAsync(identityImage);
            await TryRemoveImageAsync(notificationsImage);
        }
    }

    [Fact]
    public async Task Compose_key_generator_creates_isolated_external_keys_without_printing_secrets()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "ouroboros-compose-keys-" + Guid.NewGuid().ToString("N"));
        try
        {
            var start = new ProcessStartInfo(RabbitMqFixture.ResolvePowerShellExecutable())
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(RepositoryRoot.Resolve("scripts", "messaging", "new-compose-keys.ps1"));
            start.ArgumentList.Add("-Directory");
            start.ArgumentList.Add(tempRoot);

            using var process = Process.Start(start) ?? throw new InvalidOperationException("Script não iniciou.");
            var output = await process.StandardOutput.ReadToEndAsync(Cancellation) + await process.StandardError.ReadToEndAsync(Cancellation);
            await process.WaitForExitAsync(Cancellation);

            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain("BEGIN PRIVATE KEY", output, StringComparison.Ordinal);

            var identityFiles = Directory.GetFiles(Path.Combine(tempRoot, "identity")).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
            var notificationsFiles = Directory.GetFiles(Path.Combine(tempRoot, "notifications")).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);

            Assert.Contains("ring.json", identityFiles);
            Assert.Contains("public.json", identityFiles);
            Assert.Contains("supersession.key", identityFiles);
            Assert.DoesNotContain("private.json", identityFiles);
            Assert.DoesNotContain("fingerprint.json", identityFiles);

            Assert.Contains("private.json", notificationsFiles);
            Assert.Contains("fingerprint.json", notificationsFiles);
            Assert.DoesNotContain("ring.json", notificationsFiles);
            Assert.DoesNotContain("supersession.key", notificationsFiles);

            using var ringJson = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(tempRoot, "identity", "ring.json"), Cancellation));
            Assert.StartsWith("/run/ouroboros-keys/identity/", ringJson.RootElement.GetProperty("privateKeyPath").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private Process StartWorkerWithHealthPort(NotificationTestKeys keys, string connectionString, int healthPort)
    {
        var configuration = typeof(ServiceReadinessAndContainerTests).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), false)
            .Cast<System.Reflection.AssemblyConfigurationAttribute>()
            .Single().Configuration;
        var path = RepositoryRoot.Resolve("src", "Services", "Notifications", "Ouroboros.Notifications.Worker", "bin", configuration, "net10.0", "Ouroboros.Notifications.Worker.dll");
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(path);
        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("Identity__", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Notifications__", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("ConnectionStrings__", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            start.Environment.Remove(name);
        }

        start.Environment["DOTNET_ENVIRONMENT"] = "Development";
        start.Environment["Notifications__Health__Urls__0"] = $"http://127.0.0.1:{healthPort}";
        start.Environment["Notifications__Consumer__Enabled"] = "true";
        start.Environment["Notifications__Email__Provider"] = "PickupDirectory";
        start.Environment["Notifications__Email__PickupDirectory"] = keys.EmailsPath;
        start.Environment["Notifications__PrivateKeyRingPath"] = keys.RingPath;
        start.Environment["Notifications__FingerprintKeyRingPath"] = keys.FingerprintRingPath;
        start.Environment["ConnectionStrings__Notifications"] = connectionString;
        start.Environment["Notifications__Broker__Host"] = broker.Host;
        start.Environment["Notifications__Broker__Port"] = broker.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["Notifications__Broker__Password"] = broker.ConsumerPassword;
        start.Environment["Notifications__FailuresBroker__Host"] = broker.Host;
        start.Environment["Notifications__FailuresBroker__Port"] = broker.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["Notifications__FailuresBroker__Password"] = broker.FailuresPassword;
        return Process.Start(start) ?? throw new InvalidOperationException("Worker não iniciou.");
    }

    private static async Task WaitForHttpStatusAsync(HttpClient client, string path, HttpStatusCode expected)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            try
            {
                using var response = await client.GetAsync(path, timeout.Token);
                if (response.StatusCode == expected)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(150, timeout.Token);
        }
    }

    private static async Task StopProcessAsync(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        await process.WaitForExitAsync(Cancellation);
    }

    private static int FreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<string> RunDockerAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo("docker")
        {
            WorkingDirectory = RepositoryRoot.Resolve(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Docker não iniciou.");
        var stdout = process.StandardOutput.ReadToEndAsync(Cancellation);
        var stderr = process.StandardError.ReadToEndAsync(Cancellation);
        await process.WaitForExitAsync(Cancellation);
        var outText = await stdout;
        var errText = await stderr;
        Assert.True(process.ExitCode == 0, $"Comando docker falhou ({string.Join(' ', arguments)}): {errText}");
        return outText;
    }

    private static async Task TryRemoveImageAsync(string image)
    {
        try
        {
            await RunDockerAsync("rmi", "-f", image);
        }
        catch
        {
        }
    }
}
