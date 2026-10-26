using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using Ouroboros.Observability;

namespace Ouroboros.ArchitectureTests;

// Host genérico com a configuração comum de diagnóstico e dois destinos de teste síncronos, depois da
// sanitização: o exportador real do console JSON (escrevendo em memória) e uma cópia dos registros exportados.
internal sealed class DiagnosticsHost : IDisposable
{
    private readonly IHost _host;

    internal DiagnosticsHost(IReadOnlyDictionary<string, string?>? settings = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Production });
        builder.Configuration.AddInMemoryCollection(settings ?? new Dictionary<string, string?>());
        builder.AddOuroborosObservability("identity", typeof(DiagnosticsHost).Assembly);
        builder.Services.ConfigureOpenTelemetryLoggerProvider(logging => logging
            .AddProcessor(provider => new SimpleLogRecordExportProcessor(new JsonConsoleLogExporter(provider.GetRequiredService<ServiceIdentity>(), Console)))
            .AddProcessor(new SimpleLogRecordExportProcessor(new RecordingExporter(Records))));
        _host = builder.Build();
    }

    internal StringWriter Console { get; } = new();

    internal List<ExportedLog> Records { get; } = [];

    internal IServiceProvider Services => _host.Services;

    internal ILogger Logger(string category = "Probe.Category") => _host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(category);

    internal JsonElement[] ConsoleLines() => Console.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();

    public void Dispose() => _host.Dispose();

    private sealed class RecordingExporter(List<ExportedLog> records) : BaseExporter<LogRecord>
    {
        public override ExportResult Export(in Batch<LogRecord> batch)
        {
            foreach (var record in batch)
            {
                lock (records)
                {
                    records.Add(new ExportedLog(record.FormattedMessage, record.Body, record.Exception,
                        (record.Attributes ?? []).ToArray(), record.TraceId.ToHexString(), record.SpanId.ToHexString()));
                }
            }
            return ExportResult.Success;
        }
    }
}

internal sealed record ExportedLog(string? Message, string? Body, Exception? Exception, KeyValuePair<string, object?>[] Attributes, string TraceId, string SpanId)
{
    internal object? Attribute(string key) => Attributes.SingleOrDefault(pair => pair.Key == key).Value;
}
