using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Volumes;
using RabbitMQ.Client;

namespace Ouroboros.IntegrationTests;

public sealed class RabbitMqFixture : IAsyncLifetime
{
    internal const string Exchange = "ouroboros.notifications";
    internal const string Queue = "notifications.email.v1";
    internal const string FailuresExchange = "ouroboros.notification-failures";
    internal const string FailuresQueue = "notifications.failures.v1";
    private readonly string _adminPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly int _amqpPort = FreeTcpPort();
    private readonly int _managementPort = FreeTcpPort();
    internal readonly string IdentityPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    internal readonly string ConsumerPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    internal readonly string FailuresPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly IContainer _container;
    private readonly IVolume _volume = new VolumeBuilder().Build();
    internal string Host => _container.Hostname;
    internal int Port => _amqpPort;

    public RabbitMqFixture()
    {
        _container = new ContainerBuilder("rabbitmq:4.4.0-management")
            .WithEnvironment("RABBITMQ_DEFAULT_USER", "test_operator")
            .WithEnvironment("RABBITMQ_DEFAULT_PASS", _adminPassword)
            .WithEnvironment("RABBITMQ_DEFAULT_VHOST", "ouroboros")
            .WithVolumeMount(_volume, "/var/lib/rabbitmq")
            .WithPortBinding(_amqpPort, 5672).WithPortBinding(_managementPort, 15672)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(15672))
            .Build();
    }

    public async ValueTask InitializeAsync()
    {
        await _volume.CreateAsync();
        await _container.StartAsync();
        await ProvisionAsync();
    }

    internal static string ResolvePowerShellExecutable()
    {
        var cachedPwsh = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache", "codex-runtimes", "codex-primary-runtime", "dependencies", "native", "powershell", "pwsh.exe");
        return File.Exists(cachedPwsh) ? cachedPwsh : "pwsh";
    }

    private static int FreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    internal async Task ProvisionAsync(bool useEnvFile = false)
    {
        var start = new ProcessStartInfo(ResolvePowerShellExecutable())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(RepositoryRoot.Resolve("scripts", "messaging", "provision-rabbitmq.ps1"));
        start.ArgumentList.Add("-ManagementUrl");
        start.ArgumentList.Add($"http://{_container.Hostname}:{_container.GetMappedPublicPort(15672)}");
        start.ArgumentList.Add("-LocalDevelopment");
        start.Environment["RABBITMQ_ADMIN_USER"] = "test_operator";
        start.Environment["RABBITMQ_ADMIN_PASSWORD"] = _adminPassword;
        start.Environment["IDENTITY_BROKER_PASSWORD"] = IdentityPassword;
        start.Environment["NOTIFICATIONS_BROKER_PASSWORD"] = ConsumerPassword;
        start.Environment["NOTIFICATIONS_FAILURES_BROKER_PASSWORD"] = FailuresPassword;
        string? environmentFile = null;
        try
        {
            if (useEnvFile)
            {
                environmentFile = Path.GetTempFileName();
                var variables = new[] { "RABBITMQ_ADMIN_USER", "RABBITMQ_ADMIN_PASSWORD", "IDENTITY_BROKER_PASSWORD",
                    "NOTIFICATIONS_BROKER_PASSWORD", "NOTIFICATIONS_FAILURES_BROKER_PASSWORD" };
                var lines = variables.Select(name => $"{name}=\"{start.Environment[name]}\"").ToList();
                lines.Add("UNRELATED_VARIABLE=ignored");
                await File.WriteAllLinesAsync(environmentFile, lines, TestContext.Current.CancellationToken);
                foreach (var name in variables) { start.Environment.Remove(name); }
                start.ArgumentList.Add("-EnvFile");
                start.ArgumentList.Add(environmentFile);
            }

            using var process = Process.Start(start) ?? throw new InvalidOperationException("Provisionador não iniciado.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            var text = await output + await error;
            Assert.True(process.ExitCode == 0, "Falha no provisionador (saída sensível omitida).");
            foreach (var password in new[] { _adminPassword, IdentityPassword, ConsumerPassword, FailuresPassword })
            {
                Assert.DoesNotContain(password, text);
            }
        }
        finally
        {
            if (environmentFile is not null) { File.Delete(environmentFile); }
        }
    }

    internal Task<IConnection> ConnectAsync(string user = "test_operator", string? password = null) =>
        new ConnectionFactory
        {
            HostName = _container.Hostname,
            Port = _container.GetMappedPublicPort(5672),
            UserName = user,
            Password = password ?? _adminPassword,
            VirtualHost = "ouroboros",
            AutomaticRecoveryEnabled = false,
            RequestedConnectionTimeout = TimeSpan.FromSeconds(10),
        }.CreateConnectionAsync(TestContext.Current.CancellationToken);

    internal HttpClient ManagementClient()
    {
        var client = new HttpClient { BaseAddress = new Uri($"http://{_container.Hostname}:{_container.GetMappedPublicPort(15672)}/api/") };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("test_operator:" + _adminPassword)));
        return client;
    }

    internal async Task RestartAsync()
    {
        await _container.StopAsync();
        await _container.StartAsync();
    }

    internal Task StopAsync() => _container.StopAsync();
    internal Task StartAsync() => _container.StartAsync();

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
        await _volume.DeleteAsync();
    }
}
