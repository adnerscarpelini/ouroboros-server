using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using DotNet.Testcontainers.Volumes;

namespace Ouroboros.IntegrationTests;

// Coletor e destino dedicado reais, com as mesmas configurações do Compose (config/observability), em rede
// própria de teste. Os arquivos do destino são lidos pela API do Docker, sem shell na imagem.
public sealed class DiagnosticsPipelineFixture : IAsyncLifetime
{
    internal const string Image = "otel/opentelemetry-collector-contrib:0.162.0";
    internal const string StoreFile = "/var/lib/ouroboros-diagnostics/logs.jsonl";
    private INetwork? _network;
    private IVolume? _volume;
    private IContainer? _store;
    private IContainer? _collector;

    internal string OtlpEndpoint => $"http://{Collector.Hostname}:{Collector.GetMappedPublicPort(4318)}/";

    internal IContainer Collector => _collector ?? throw new InvalidOperationException("Coletor não iniciado.");

    internal IContainer Store => _store ?? throw new InvalidOperationException("Destino não iniciado.");

    public async ValueTask InitializeAsync()
    {
        _network = new NetworkBuilder().WithName("ouroboros-diagnostics-" + Guid.NewGuid().ToString("N")).Build();
        await _network.CreateAsync(TestContext.Current.CancellationToken);
        // Como no Compose: o volume novo nasce do root e só o dono é ajustado ao usuário sem privilégios 10001.
        _volume = new VolumeBuilder().Build();
        await _volume.CreateAsync(TestContext.Current.CancellationToken);
        await using (var init = new ContainerBuilder("busybox:1.37.0").WithVolumeMount(_volume, "/data")
            .WithCommand("sh", "-c", "chown 10001:10001 /data && chmod 0750 /data && echo ready && sleep 600")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("ready")).Build())
        {
            await init.StartAsync(TestContext.Current.CancellationToken);
        }
        _store = CollectorBuilder("diagnostics-store.yaml").WithNetworkAliases("diagnostics-store").WithVolumeMount(_volume, "/var/lib/ouroboros-diagnostics").Build();
        await _store.StartAsync(TestContext.Current.CancellationToken);
        _collector = CollectorBuilder("collector.yaml").WithPortBinding(4318, true).Build();
        await _collector.StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_collector is not null) { await _collector.DisposeAsync(); }
        if (_store is not null) { await _store.DisposeAsync(); }
        if (_network is not null) { await _network.DisposeAsync(); }
        if (_volume is not null) { await _volume.DeleteAsync(); await _volume.DisposeAsync(); }
    }

    // Linhas OTLP/JSON gravadas pelo destino (uma por lote recebido); vazio enquanto nada chegou.
    internal async Task<string> StoredTextAsync(string signal = "logs")
    {
        try { return Encoding.UTF8.GetString(await Store.ReadFileAsync($"/var/lib/ouroboros-diagnostics/{signal}.jsonl", TestContext.Current.CancellationToken)); }
        catch (Exception exception) when (exception is not OperationCanceledException) { return ""; }
    }

    internal async Task<JsonElement[]> StoredRecordsAsync() =>
        (await StoredTextAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(line =>
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.GetProperty("resourceLogs").EnumerateArray().SelectMany(resource =>
                    resource.GetProperty("scopeLogs").EnumerateArray().SelectMany(scope => scope.GetProperty("logRecords").EnumerateArray()
                        .Select(record => JsonSerializer.SerializeToElement(new { resource = resource.GetProperty("resource"), record })))).ToArray();
            }).ToArray();

    internal async Task<JsonElement> WaitForRecordAsync(Func<JsonElement, bool> match)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            var found = (await StoredRecordsAsync()).FirstOrDefault(match);
            if (found.ValueKind != JsonValueKind.Undefined) { return found; }
            await Task.Delay(250, timeout.Token);
        }
    }

    private ContainerBuilder CollectorBuilder(string configuration) => new ContainerBuilder(Image)
        .WithNetwork(_network!)
        .WithResourceMapping(File.ReadAllBytes(RepositoryRoot.Resolve("config", "observability", configuration)), "/etc/otelcol/" + configuration)
        .WithCommand("--config=/etc/otelcol/" + configuration)
        .WithPortBinding(13133, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(13133)));
}

internal static class OtlpJson
{
    internal static string? Attribute(JsonElement element, string key) =>
        element.TryGetProperty("attributes", out var attributes)
            ? attributes.EnumerateArray().Where(attribute => attribute.GetProperty("key").GetString() == key)
                .Select(attribute => attribute.GetProperty("value").EnumerateObject().First().Value.ToString()).FirstOrDefault()
            : null;
}
