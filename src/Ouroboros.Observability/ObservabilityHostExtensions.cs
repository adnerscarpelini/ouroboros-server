using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Ouroboros.Observability;

// Configuração comum de diagnóstico das APIs e dos workers: ILogger -> OpenTelemetry, com sanitização antes de
// qualquer destino, console JSON (compatível com Linux e containers, sem Windows Event Log) e, quando
// configurado, o coletor OTLP. Também aplica o contexto W3C às entradas HTTP e ao processamento de mensagens.
// Não conhece serviços, bancos nem regras de negócio, e não grava em banco algum.
public static class ObservabilityHostExtensions
{
    // Filas limitadas por destino: cheias, descartam novos registros em vez de bloquear quem chamou o ILogger.
    internal const int MaximumQueueSize = 2048;
    internal const int MaximumExportBatchSize = 512;
    internal const int ExportTimeoutMilliseconds = 5000;

    public static IHostApplicationBuilder AddOuroborosObservability(this IHostApplicationBuilder builder, string serviceName, Assembly serviceAssembly)
    {
        var service = ServiceIdentity.From(serviceName, serviceAssembly);
        builder.Services.AddSingleton(service);
        builder.Services.AddSingleton<IValidateOptions<ObservabilityOptions>, ObservabilityOptionsValidator>();
        builder.Services.AddOptions<ObservabilityOptions>().BindConfiguration(ObservabilityOptions.SectionName).ValidateOnStart();
        TelemetryFailureSignal.EnsureStarted();

        // Remove os provedores padrão (no Windows, inclusive o Event Log): o OpenTelemetry é o único provedor.
        builder.Logging.ClearProviders();
        builder.Logging.Configure(options => options.ActivityTrackingOptions = ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId);
        var instance = Guid.NewGuid().ToString("D");
        var environment = builder.Environment.EnvironmentName;
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(service.Name, serviceVersion: service.Version, autoGenerateServiceInstanceId: false, serviceInstanceId: instance)
                .AddAttributes([new("deployment.environment.name", environment)]))
            .WithTracing(tracing => tracing
                .SetSampler(new AlwaysOnSampler())
                .AddSource("Ouroboros.*")
                .AddAspNetCoreInstrumentation(options =>
                {
                    options.RecordException = false;
                    options.EnrichWithHttpResponse = (activity, _) =>
                    {
                        activity.SetTag("url.query", null);
                        activity.SetTag("url.full", null);
                    };
                })
                .AddProcessor(new SanitizingSpanProcessor())
                .AddProcessor(provider =>
                {
                    var endpoint = Endpoint(provider, "traces");
                    return endpoint is null ? new DisabledSpanProcessor() : new BatchActivityExportProcessor(
                        new OtlpTraceExporter(ExporterOptions(endpoint)), MaximumQueueSize, 1000,
                        ExportTimeoutMilliseconds, MaximumExportBatchSize);
                }))
            .WithMetrics(metrics => metrics
                .AddMeter("Ouroboros.*", "Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel")
                // Um único conjunto permitido também remove dimensões acrescentadas pelo framework no futuro.
                .AddView(instrument => new MetricStreamConfiguration
                {
                    TagKeys = instrument.Meter.Name.StartsWith("Ouroboros.", StringComparison.Ordinal)
                        ? ["code", "state", "result", "outcome"] : ["http.route", "http.request.method", "http.response.status_code"],
                    CardinalityLimit = 256,
                })
                .AddReader(provider =>
                {
                    var endpoint = Endpoint(provider, "metrics");
                    return new PeriodicExportingMetricReader(endpoint is null
                        ? new DisabledMetricExporter() : new OtlpMetricExporter(ExporterOptions(endpoint)), 1000, ExportTimeoutMilliseconds);
                }))
            .WithLogging(
                logging => logging
                    .AddProcessor(new SanitizingLogProcessor())
                    .AddProcessor(provider => Batch(new JsonConsoleLogExporter(provider.GetRequiredService<ServiceIdentity>())))
                    .AddProcessor(OtlpProcessor),
                options =>
                {
                    // Escopos e o estado original não são exportados: só os campos sanitizados do registro.
                    options.IncludeScopes = false;
                    // O formatter do ILogger não é chamado (ToString de objetos poderia lançar ou vazar): a mensagem é
                    // recomposta do template pelo SanitizingLogProcessor.
                    options.IncludeFormattedMessage = false;
                    options.ParseStateValues = true;
                });

        // Só o formato W3C (traceparent/tracestate): Request-Id e outros formatos legados não iniciam contexto.
        builder.Services.RemoveAll<DistributedContextPropagator>();
        builder.Services.AddSingleton(DistributedContextPropagator.CreateW3CPropagator());
        builder.Services.AddHostedService<TracePropagationListener>();
        return builder;
    }

    private static BaseProcessor<LogRecord> OtlpProcessor(IServiceProvider provider)
    {
        var endpoint = provider.GetRequiredService<IOptions<ObservabilityOptions>>().Value.Otlp.Endpoint;
        if (string.IsNullOrWhiteSpace(endpoint)) { return new DisabledLogProcessor(); }
        return Batch(new OtlpLogExporter(new OtlpExporterOptions
        {
            Endpoint = new Uri(new Uri(endpoint), "v1/logs"),
            Protocol = OtlpExportProtocol.HttpProtobuf,
            TimeoutMilliseconds = ExportTimeoutMilliseconds,
        }));
    }

    private static BatchLogRecordExportProcessor Batch(BaseExporter<LogRecord> exporter) =>
        new(exporter, MaximumQueueSize, scheduledDelayMilliseconds: 1000, ExportTimeoutMilliseconds, MaximumExportBatchSize);

    private static Uri? Endpoint(IServiceProvider provider, string signal)
    {
        var endpoint = provider.GetRequiredService<IOptions<ObservabilityOptions>>().Value.Otlp.Endpoint;
        return string.IsNullOrWhiteSpace(endpoint) ? null : new Uri(new Uri(endpoint), "v1/" + signal);
    }

    private static OtlpExporterOptions ExporterOptions(Uri endpoint) => new()
    {
        Endpoint = endpoint,
        Protocol = OtlpExportProtocol.HttpProtobuf,
        TimeoutMilliseconds = ExportTimeoutMilliseconds,
    };

    private sealed class DisabledSpanProcessor : BaseProcessor<Activity>;
    private sealed class DisabledMetricExporter : BaseExporter<Metric>
    {
        public override ExportResult Export(in Batch<Metric> batch) => ExportResult.Success;
    }
    private sealed class DisabledLogProcessor : BaseProcessor<LogRecord>;
}
