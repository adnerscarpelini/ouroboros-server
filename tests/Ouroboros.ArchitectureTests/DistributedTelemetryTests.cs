using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Ouroboros.Messaging;
using Ouroboros.Observability;

namespace Ouroboros.ArchitectureTests;

public sealed class DistributedTelemetryTests
{
    [Fact]
    public async Task Exported_spans_keep_origin_links_and_remove_sensitive_attributes_before_export()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddOuroborosObservability("identity", typeof(DistributedTelemetryTests).Assembly);
        var spans = new List<Activity>();
        builder.Services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddProcessor(new SimpleActivityExportProcessor(new RecordingSpans(spans))));
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var source = new ActivitySource("Ouroboros.Test.Distributed");
        const string trace = "4bf92f3577b34da6a3ce929d0e0e4736";
        const string parent = $"00-{trace}-00f067aa0ba902b7-00";
        using (var batch = MessageTracing.StartBatch(source, [(parent, "vendor=external-secret")]))
        {
            using var message = MessageTracing.Start(source, "outbox.publish", ActivityKind.Producer, parent, "vendor=external-secret");
            Assert.NotNull(message);
            Assert.True(message.Recorded);
            Assert.Equal(trace, message.TraceId.ToString());
            Assert.NotEqual(batch!.TraceId, message.TraceId);
            message.SetTag("url.full", "https://example.com/reset?token=secret");
            message.SetTag("url.query", "?token=secret");
            message.SetTag("Password", "hunter2");
            message.SetTag("enduser.id", "customer-id");
            message.SetTag("http.request.header.authorization", "Bearer abcdef");
            message.SetTag("safe", "customer@example.com password=hunter2");
        }
        var exported = Assert.Single(spans, span => span.DisplayName == "outbox.publish");
        Assert.Single(exported.Links);
        Assert.Null(exported.TraceStateString);
        Assert.Null(Assert.Single(exported.Links).Context.TraceState);
        Assert.All(new[] { "url.full", "url.query", "Password", "enduser.id", "http.request.header.authorization" }, key => Assert.Null(exported.GetTagItem(key)));
        Assert.Equal("[EMAIL] password=[REDACTED]", exported.GetTagItem("safe"));
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Metric_views_remove_identifiers_and_limit_dimensions_for_framework_and_application()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddOuroborosObservability("identity", typeof(DistributedTelemetryTests).Assembly);
        var points = new List<KeyValuePair<string, object?>[]>();
        builder.Services.ConfigureOpenTelemetryMeterProvider(metrics => metrics.AddReader(new PeriodicExportingMetricReader(new RecordingMetrics(points), 60000)));
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var meter = new Meter("Ouroboros.Test.Restricted");
        var counter = meter.CreateCounter<long>("probe.dimensions");
        counter.Add(1, new TagList { { "result", "Accepted" }, { "email", "secret@example.com" }, { "user.id", "opaque-user" }, { "token", "secret" } });
        host.Services.GetRequiredService<MeterProvider>().ForceFlush();
        var tags = Assert.Single(points);
        var tag = Assert.Single(tags);
        Assert.Equal("result", tag.Key);
        Assert.Equal("Accepted", tag.Value);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Deliberately_unsubscribed_meter_is_not_reported_as_a_pipeline_failure()
    {
        using var output = new StringWriter();
        using var signal = new TelemetryFailureSignal(TextWriter.Synchronized(output), TimeSpan.Zero);
        var builder = Host.CreateApplicationBuilder();
        builder.AddOuroborosObservability("notifications", typeof(DistributedTelemetryTests).Assembly);
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var meter = new Meter("Excluded.Test.Meter");
        meter.CreateCounter<long>("excluded.measurement").Add(1);
        Assert.DoesNotContain("excluded.measurement", output.ToString());
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    private sealed class RecordingSpans(List<Activity> spans) : BaseExporter<Activity>
    {
        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch) { if (activity.Source.Name == "Ouroboros.Test.Distributed") { spans.Add(activity); } }
            return ExportResult.Success;
        }
    }

    private sealed class RecordingMetrics(List<KeyValuePair<string, object?>[]> points) : BaseExporter<Metric>
    {
        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (var metric in batch)
            {
                if (metric.Name != "probe.dimensions") { continue; }
                foreach (ref readonly var point in metric.GetMetricPoints())
                {
                    var tags = new List<KeyValuePair<string, object?>>();
                    foreach (var tag in point.Tags) { tags.Add(tag); }
                    points.Add(tags.ToArray());
                }
            }
            return ExportResult.Success;
        }
    }
}
