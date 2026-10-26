using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ouroboros.Observability;

namespace Ouroboros.ArchitectureTests;

public sealed partial class TraceCorrelationTests(OuroborosApiFactory factory) : IClassFixture<OuroborosApiFactory>
{
    private const string ReceivedTraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
    private const string ReceivedSpanId = "00f067aa0ba902b7";
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Request_without_traceparent_gets_one_trace_shared_by_all_its_logs_and_problem_details()
    {
        var (body, logs) = await FailAsync(headers: []);

        var (traceId, spanId) = Segments(body);
        var requestLogs = logs.Where(entry => entry.Category.EndsWith(nameof(CorrelationProbeController), StringComparison.Ordinal)
            || entry.Level == LogLevel.Error).ToArray();
        Assert.Equal(3, requestLogs.Length);
        Assert.All(requestLogs, entry => Assert.Equal(traceId, entry.TraceId));
        Assert.All(requestLogs, entry => Assert.Equal(spanId, entry.SpanId));
        Assert.NotEqual(traceId, spanId);
    }

    [Fact]
    public async Task Valid_traceparent_is_continued_by_logs_and_problem_details_with_a_new_span()
    {
        var (body, logs) = await FailAsync(("traceparent", $"00-{ReceivedTraceId}-{ReceivedSpanId}-01"));

        var (traceId, spanId) = Segments(body);
        Assert.Equal(ReceivedTraceId, traceId);
        Assert.NotEqual(ReceivedSpanId, spanId);
        Assert.All(logs.Where(entry => entry.TraceId is not null), entry => Assert.Equal(ReceivedTraceId, entry.TraceId));
    }

    [Theory]
    [InlineData("traceparent", "garbage")]
    [InlineData("traceparent", "00-00000000000000000000000000000000-00f067aa0ba902b7-01")]
    [InlineData("traceparent", "00-4bf92f3577b34da6a3ce929d0e0e4736-0000000000000000-01")]
    [InlineData("traceparent", "00-4BF92F3577B34DA6A3CE929D0E0E4736-00F067AA0BA902B7-01")]
    [InlineData("traceparent", "ff-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01")]
    [InlineData("traceparent", "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7")]
    [InlineData("Request-Id", "|4bf92f3577b34da6a3ce929d0e0e4736.00f067aa0ba902b7.")]
    public async Task Invalid_or_legacy_context_is_replaced_by_a_new_valid_w3c_trace(string header, string value)
    {
        var (body, logs) = await FailAsync((header, value));

        var (traceId, _) = Segments(body);
        Assert.NotEqual(ReceivedTraceId, traceId);
        Assert.NotEqual(new string('0', 32), traceId);
        var error = Assert.Single(logs, entry => entry.Level == LogLevel.Error);
        Assert.Equal(traceId, error.TraceId);
    }

    [Fact]
    public async Task Received_trace_context_does_not_authenticate_and_is_only_correlated()
    {
        using var client = ProbeHost.CreateClient(factory, new ProbeCallCounter());
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/secured-probe");
        request.Headers.Add("traceparent", $"00-{ReceivedTraceId}-{ReceivedSpanId}-01");

        using var response = await client.SendAsync(request, Cancellation);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var (traceId, _) = Segments(await response.Content.ReadAsStringAsync(Cancellation));
        Assert.Equal(ReceivedTraceId, traceId);
    }

    [Fact]
    public void Identity_api_logs_only_through_the_diagnostic_pipeline_without_windows_event_log()
    {
        using var host = factory.WithWebHostBuilder(_ => { });
        AssertJsonConsoleOnly(host.Services, "identity");
    }

    [Fact]
    public void Worker_hosts_get_the_same_logging_and_propagation_configuration()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Production });
        builder.AddOuroborosObservability("notifications", typeof(TraceCorrelationTests).Assembly);
        using var host = builder.Build();

        AssertJsonConsoleOnly(host.Services, "notifications");
        Assert.Equal(DistributedContextPropagator.CreateW3CPropagator().GetType(), host.Services.GetRequiredService<DistributedContextPropagator>().GetType());
    }

    [Theory]
    [InlineData("Customer")]
    [InlineData("identity.api")]
    [InlineData("")]
    public void Service_name_must_be_a_lowercase_identifier(string name)
    {
        var builder = Host.CreateApplicationBuilder();
        Assert.Throws<ArgumentException>(() => builder.AddOuroborosObservability(name, typeof(TraceCorrelationTests).Assembly));
    }

    [Fact]
    public void Json_console_line_separates_trace_span_service_version_and_occurrence()
    {
        using var diagnostics = new DiagnosticsHost();
        using var activity = new Activity("probe").SetIdFormat(ActivityIdFormat.W3C).Start();

        LogProcessing(diagnostics.Logger(), Guid.Parse("0196b2a0-0000-7000-8000-000000000001"), 3);
        activity.Stop();

        var text = diagnostics.Console.ToString();
        Assert.DoesNotContain(activity.Id!, text, StringComparison.Ordinal);
        var root = Assert.Single(diagnostics.ConsoleLines());
        Assert.Equal("identity", root.GetProperty("service").GetString());
        Assert.Equal(diagnostics.Services.GetRequiredService<ServiceIdentity>().Version, root.GetProperty("serviceVersion").GetString());
        Assert.Equal(activity.TraceId.ToHexString(), root.GetProperty("traceId").GetString());
        Assert.Equal(activity.SpanId.ToHexString(), root.GetProperty("spanId").GetString());
        Assert.Equal("Warning", root.GetProperty("level").GetString());
        Assert.Equal("Probe.Category", root.GetProperty("category").GetString());
        Assert.Equal(7, root.GetProperty("eventId").GetInt32());
        Assert.Equal(TimeSpan.Zero, root.GetProperty("timestamp").GetDateTimeOffset().Offset);
        Assert.Equal("Processamento de 0196b2a0-0000-7000-8000-000000000001: 3.", root.GetProperty("message").GetString());
        Assert.Equal(3, root.GetProperty("attributes").GetProperty("Attempts").GetInt32());
        Assert.Equal("0196b2a0-0000-7000-8000-000000000001", root.GetProperty("attributes").GetProperty("MessageId").GetString());
        Assert.False(root.GetProperty("attributes").TryGetProperty("{OriginalFormat}", out _));
        var occurrence = Guid.Parse(root.GetProperty("occurrenceId").GetString()!);
        Assert.NotEqual(activity.TraceId.ToHexString(), occurrence.ToString("N"));
    }

    [Fact]
    public void Json_console_line_outside_any_activity_has_no_invented_trace()
    {
        using var diagnostics = new DiagnosticsHost();
        var previous = Activity.Current;
        Activity.Current = null;
        try
        {
            diagnostics.Logger().LogInformation("texto");
            var root = Assert.Single(diagnostics.ConsoleLines());
            Assert.False(root.TryGetProperty("traceId", out _));
            Assert.False(root.TryGetProperty("spanId", out _));
        }
        finally { Activity.Current = previous; }
    }

    private static void AssertJsonConsoleOnly(IServiceProvider services, string service)
    {
        var providers = services.GetServices<ILoggerProvider>().Select(provider => provider.GetType().Name).ToArray();
        Assert.Equal(["OpenTelemetryLoggerProvider"], providers);
        Assert.Equal(ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId,
            services.GetRequiredService<IOptions<LoggerFactoryOptions>>().Value.ActivityTrackingOptions);
        Assert.Equal(service, services.GetRequiredService<ServiceIdentity>().Name);
        Assert.False(string.IsNullOrWhiteSpace(services.GetRequiredService<ServiceIdentity>().Version));
    }

    private async Task<(string Body, CapturedLog[] Logs)> FailAsync(params (string Name, string Value)[] headers)
    {
        var logs = new CapturingLoggerProvider();
        using var client = ProbeHost.CreateClient(factory, new ProbeCallCounter(), logs);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/correlation-probe");
        foreach (var (name, value) in headers) { request.Headers.TryAddWithoutValidation(name, value); }
        using var response = await client.SendAsync(request, Cancellation);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        return (await response.Content.ReadAsStringAsync(Cancellation), logs.Entries.ToArray());
    }

    // O traceId do ProblemDetails é o identificador W3C completo; TraceId e SpanId dos logs são seus segmentos.
    private static (string TraceId, string SpanId) Segments(string body)
    {
        using var document = JsonDocument.Parse(body);
        var match = W3CId().Match(document.RootElement.GetProperty("traceId").GetString()!);
        Assert.True(match.Success);
        return (match.Groups["trace"].Value, match.Groups["span"].Value);
    }

    [GeneratedRegex("^00-(?<trace>[0-9a-f]{32})-(?<span>[0-9a-f]{16})-[0-9a-f]{2}$")]
    private static partial Regex W3CId();

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "Processamento de {MessageId}: {Attempts}.")]
    private static partial void LogProcessing(ILogger logger, Guid messageId, int attempts);
}

[ApiController]
[AllowAnonymous]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/correlation-probe")]
public sealed partial class CorrelationProbeController(ILogger<CorrelationProbeController> logger) : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        LogStep(logger, 1);
        LogStep(logger, 2);
        throw new InvalidOperationException("falha de sonda");
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Etapa {Step} da sonda de correlação.")]
    private static partial void LogStep(ILogger logger, int step);
}
