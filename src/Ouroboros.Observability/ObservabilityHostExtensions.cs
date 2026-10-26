using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace Ouroboros.Observability;

// Configuração comum de correlação das APIs e dos workers: logs estruturados no console (compatível com
// Linux e containers, sem Windows Event Log) e contexto W3C nas entradas HTTP e no processamento de mensagens.
// Não conhece serviços, bancos nem regras de negócio.
public static class ObservabilityHostExtensions
{
    public static IHostApplicationBuilder AddOuroborosObservability(this IHostApplicationBuilder builder, string serviceName, Assembly serviceAssembly)
    {
        var service = ServiceIdentity.From(serviceName, serviceAssembly);
        builder.Services.AddSingleton(service);

        // Remove os provedores padrão (no Windows, inclusive o Event Log): o console JSON é a única saída.
        builder.Logging.ClearProviders();
        builder.Logging.Configure(options => options.ActivityTrackingOptions = ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId);
        builder.Logging.AddConsole(options => options.FormatterName = JsonLogFormatter.FormatterName);
        builder.Logging.AddConsoleFormatter<JsonLogFormatter, ConsoleFormatterOptions>();

        // Só o formato W3C (traceparent/tracestate): Request-Id e outros formatos legados não iniciam contexto.
        builder.Services.RemoveAll<DistributedContextPropagator>();
        builder.Services.AddSingleton(DistributedContextPropagator.CreateW3CPropagator());
        builder.Services.AddHostedService<TracePropagationListener>();
        return builder;
    }
}
