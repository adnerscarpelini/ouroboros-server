using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Docker.DotNet;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Ouroboros.IntegrationTests;

[Collection("Telemetry")]
public sealed class CollectorRecoveryTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;
    private static string Configuration => File.ReadAllText(RepositoryRoot.Resolve("config", "observability", "collector.yaml"));

    [Fact]
    public async Task Sanitized_persistent_queue_recovers_after_sigkill_and_container_replacement_with_the_same_volume()
    {
        await using var pipeline = new DiagnosticsPipelineFixture();
        await pipeline.InitializeAsync();
        await pipeline.Store.StopAsync(Cancellation);
        var marker = Guid.NewGuid().ToString("N");
        await SendAsync(pipeline.OtlpEndpoint, $"{marker} Password=queued-secret; customer@example.com");
        await WaitAsync(async () => Metric(await pipeline.InternalMetricsAsync(), "otelcol_exporter_queue_size") > 0);
        // Nenhum graceful flush: o processo recebe SIGKILL; o próximo container reutiliza só o volume.
        using var docker = new DockerClientBuilder().Build();
        await docker.Containers.KillContainerAsync(pipeline.Collector.Id, new ContainerKillParameters { Signal = "SIGKILL" }, Cancellation);
        await using var replacement = pipeline.NewCollector().Build();
        await replacement.StartAsync(Cancellation);
        await pipeline.Store.StartAsync(Cancellation);
        await pipeline.WaitForRecordAsync(item => item.GetProperty("record").GetProperty("body").ToString().Contains(marker, StringComparison.Ordinal));
        var stored = await pipeline.StoredTextAsync();
        Assert.DoesNotContain("queued-secret", stored);
        Assert.DoesNotContain("customer@example.com", stored);
        await WaitAsync(async () => Metric(await DiagnosticsPipelineFixture.InternalMetricsAsync(replacement), "otelcol_exporter_queue_size") == 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unavailable_destination_has_bounded_queue_storage_and_observable_enqueue_and_send_failures(bool fileLimit)
    {
        // Mesma configuração; limites reduzidos apenas para chegar à saturação sem carga desnecessária.
        var queueLimit = fileLimit ? 8388608 : 32768;
        var maximumFile = fileLimit ? 65536 : 33554432;
        var configuration = Configuration.Replace("queue_size: 8388608", $"queue_size: {queueLimit}", StringComparison.Ordinal)
            .Replace("max_size: 33554432", $"max_size: {maximumFile}", StringComparison.Ordinal)
            .Replace("max_elapsed_time: 300s", "max_elapsed_time: 3s", StringComparison.Ordinal)
            .Replace("max_interval: 30s", "max_interval: 1s", StringComparison.Ordinal);
        if (fileLimit)
        {
            configuration = configuration.Replace("on_rebound: true", "on_rebound: false", StringComparison.Ordinal);
        }
        await using var pipeline = new DiagnosticsPipelineFixture { CollectorConfigurationOverride = configuration };
        await pipeline.InitializeAsync();
        await pipeline.Store.StopAsync(Cancellation);
        await SendAsync(pipeline.OtlpEndpoint, "small-batch-before-overflow");
        await WaitAsync(async () => Metric(await pipeline.InternalMetricsAsync(), "otelcol_exporter_send_failed_log_records") > 0);
        for (var index = 0; index < 12; index++)
        { await SendAsync(pipeline.OtlpEndpoint, new string('x', 1024), 128, unique: fileLimit); }
        await WaitAsync(async () => Metric(await pipeline.InternalMetricsAsync(), "otelcol_exporter_enqueue_failed_log_records") > 0);
        await WaitAsync(async () => Metric(await pipeline.InternalMetricsAsync(), "otelcol_exporter_send_failed_log_records") > 0);
        var metrics = await pipeline.InternalMetricsAsync();
        Assert.InRange(Metric(metrics, "otelcol_exporter_queue_size"), 0, queueLimit);
        await using var inspect = new ContainerBuilder("busybox:1.37.0").WithVolumeMount(pipeline.QueueVolume, "/queue")
            .WithCommand("sh", "-c", "find /queue -type f -maxdepth 1 -exec stat -c %s {} +; sleep 600")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("[0-9]+")).Build();
        await inspect.StartAsync(Cancellation);
        var (sizes, _) = await inspect.GetLogsAsync(ct: Cancellation);
        var files = sizes.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, files.Length);
        Assert.All(files, size => Assert.InRange(long.Parse(size[(size.LastIndexOf(' ') + 1)..], CultureInfo.InvariantCulture), 0, maximumFile));
        await AssertBusinessAsync(pipeline.OtlpEndpoint);
    }

    [Theory]
    [InlineData("permission")]
    [InlineData("invalid")]
    public async Task Unusable_storage_fails_with_bounded_startup_and_business_keeps_its_contract(string fault)
    {
        await using var pipeline = new DiagnosticsPipelineFixture();
        await pipeline.InitializeAsync();
        var endpoint = pipeline.OtlpEndpoint;
        await pipeline.Collector.StopAsync(Cancellation);
        var command = fault == "permission" ? "chmod 000 /queue" : "for f in /queue/*; do if [ -f \"$f\" ]; then printf invalid > \"$f\"; fi; done";
        await using (var mutate = new ContainerBuilder("busybox:1.37.0").WithVolumeMount(pipeline.QueueVolume, "/queue")
            .WithCommand("sh", "-c", command + "; echo ready; sleep 600")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("ready")).Build())
        { await mutate.StartAsync(Cancellation); }
        await using var broken = pipeline.NewCollector().Build();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var started = Stopwatch.StartNew();
        Assert.NotNull(await Record.ExceptionAsync(() => broken.StartAsync(timeout.Token)));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(20));
        var (stdout, stderr) = await broken.GetLogsAsync(ct: Cancellation);
        Assert.Contains(fault == "permission" ? "permission denied" : "invalid", stdout + stderr, StringComparison.OrdinalIgnoreCase);
        Assert.True((stdout + stderr).Length < 65536, "Falha do armazenamento não deve iniciar recursão ilimitada.");
        await AssertBusinessAsync(endpoint);
    }

    [Fact]
    public async Task Full_linux_filesystem_reports_storage_rejection_and_does_not_block_business()
    {
        await using var pipeline = new DiagnosticsPipelineFixture();
        await pipeline.InitializeAsync();
        var configuration = Configuration.Replace("/var/lib/ouroboros-collector", "/queue-full", StringComparison.Ordinal)
            .Replace("on_start: true", "on_start: false", StringComparison.Ordinal);
        await using var full = pipeline.NewCollector(configuration).WithCreateParameterModifier(parameters =>
            (parameters.HostConfig ??= new HostConfig()).Tmpfs = new Dictionary<string, string> { ["/queue-full"] = "rw,size=524288,uid=10001,gid=10001,mode=0750" }).Build();
        await full.StartAsync(Cancellation);
        var endpoint = $"http://{full.Hostname}:{full.GetMappedPublicPort(4318)}/";
        for (var index = 0; index < 8; index++) { await SendAsync(endpoint, new string('x', 1024), 512); }
        await WaitAsync(async () => Metric(await DiagnosticsPipelineFixture.InternalMetricsAsync(full), "otelcol_exporter_enqueue_failed_log_records") > 0);
        var (stdout, stderr) = await full.GetLogsAsync(ct: Cancellation);
        Assert.Contains("no space left on device", stdout + stderr, StringComparison.OrdinalIgnoreCase);
        await AssertBusinessAsync(endpoint);
    }

    [Fact]
    public async Task Instances_use_distinct_volumes_and_a_second_writer_to_the_same_volume_fails_with_a_lock_timeout()
    {
        await using var first = new DiagnosticsPipelineFixture();
        await using var second = new DiagnosticsPipelineFixture();
        await first.InitializeAsync();
        await second.InitializeAsync();
        Assert.NotEqual(first.QueueVolume.Name, second.QueueVolume.Name);
        var marker = Guid.NewGuid().ToString("N");
        await SendAsync(first.OtlpEndpoint, "first-" + marker);
        await SendAsync(second.OtlpEndpoint, "second-" + marker);
        await first.WaitForRecordAsync(item => item.GetProperty("record").GetProperty("body").GetProperty("stringValue").GetString() == "first-" + marker);
        await second.WaitForRecordAsync(item => item.GetProperty("record").GetProperty("body").GetProperty("stringValue").GetString() == "second-" + marker);
        await using var contender = first.NewCollector().Build();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        Assert.NotNull(await Record.ExceptionAsync(() => contender.StartAsync(timeout.Token)));
        var (stdout, stderr) = await contender.GetLogsAsync(ct: Cancellation);
        Assert.Contains("timeout", stdout + stderr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("second-" + marker, await first.StoredTextAsync());
        Assert.DoesNotContain("first-" + marker, await second.StoredTextAsync());
    }

    private async Task AssertBusinessAsync(string endpoint)
    {
        await using var api = Api.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(configuration =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Observability:Otlp:Endpoint"] = endpoint })));
        using var client = api.CreateClient();
        var started = Stopwatch.StartNew();
        using var response = await client.PostAsJsonAsync("/api/v1/identity/registrations", new { Email = "buffer-test@example.com" }, Cancellation);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5), "Indisponibilidade do diagnóstico não deve bloquear o negócio.");
    }

    private static async Task SendAsync(string endpoint, string body, int count = 1, bool unique = false)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await http.PostAsJsonAsync(new Uri(new Uri(endpoint), "v1/logs"), new
        {
            resourceLogs = new[] { new { resource = new { }, scopeLogs = new[] { new
            {
                logRecords = Enumerable.Range(0, count).Select(_ => new { body = new { stringValue = unique ? body + Guid.NewGuid().ToString("D") : body } }).ToArray(),
            } } } },
        }, Cancellation);
        // O batch processor pode aceitar antes da falha de enqueue; 200 não é confirmação de durabilidade.
        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable });
    }

    private static double Metric(string metrics, string name) => metrics.Split('\n').Where(line => line.StartsWith(name, StringComparison.Ordinal))
        .Select(line => double.Parse(line[(line.LastIndexOf(' ') + 1)..], CultureInfo.InvariantCulture)).Sum();

    private static async Task WaitAsync(Func<Task<bool>> predicate)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (!await predicate()) { await Task.Delay(250, timeout.Token); }
    }
}
