using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ouroboros.Identity.Domain.Users;
using Ouroboros.Identity.Infrastructure.Persistence;
using Ouroboros.Observability;

namespace Ouroboros.IntegrationTests;

public sealed class DiagnosticsPipelineTests(PostgresFixture postgres, DiagnosticsPipelineFixture pipeline)
    : IntegrationTest(postgres), IClassFixture<DiagnosticsPipelineFixture>
{
    private const string TraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Sensitive_event_reaches_the_dedicated_store_sanitized_with_service_trace_and_occurrence()
    {
        await using var api = WithEndpoint(pipeline.OtlpEndpoint);
        using var client = api.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/diagnostics-probe/fail");
        request.Headers.Add("traceparent", $"00-{TraceId}-00f067aa0ba902b7-01");

        using var response = await client.SendAsync(request, Cancellation);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Cancellation);
        Assert.DoesNotContain(DiagnosticsProbeController.Password, body, StringComparison.Ordinal);
        var warning = await pipeline.WaitForRecordAsync(item => TraceOf(item) == TraceId
            && OtlpJson.Attribute(Record(item), "Marker") == DiagnosticsProbeController.Marker);
        var error = await pipeline.WaitForRecordAsync(item => TraceOf(item) == TraceId
            && OtlpJson.Attribute(Record(item), "exception.type") == typeof(InvalidOperationException).FullName);
        var stored = await pipeline.StoredTextAsync();
        foreach (var secret in DiagnosticsProbeController.Secrets) { Assert.DoesNotContain(secret, stored, StringComparison.Ordinal); }

        var resource = warning.GetProperty("resource");
        Assert.Equal("identity", OtlpJson.Attribute(resource, "service.name"));
        Assert.False(string.IsNullOrWhiteSpace(OtlpJson.Attribute(resource, "service.version")));
        Assert.True(Guid.TryParse(OtlpJson.Attribute(resource, "service.instance.id"), out _));
        Assert.Equal("Testing", OtlpJson.Attribute(resource, "deployment.environment.name"));
        var occurrences = new[] { warning, error }.Select(item => OtlpJson.Attribute(Record(item), "log.record.uid")!).ToArray();
        Assert.Equal(2, occurrences.Distinct().Count());
        Assert.All(occurrences, occurrence => Assert.NotEqual(TraceId, Guid.Parse(occurrence).ToString("N")));
        Assert.Equal(16, Record(warning).GetProperty("spanId").GetString()!.Length);
        Assert.Equal("[REDACTED]", OtlpJson.Attribute(Record(warning), "Password"));
        Assert.Equal($"Sonda {DiagnosticsProbeController.Marker}: senha [REDACTED], destinatário [REDACTED], link http://localhost/confirm?[REDACTED]",
            Record(warning).GetProperty("body").GetProperty("stringValue").GetString());
    }

    [Fact]
    public async Task Business_rollback_does_not_discard_the_diagnostic_and_no_database_receives_it()
    {
        await using var api = WithEndpoint(pipeline.OtlpEndpoint);
        using var client = api.CreateClient();
        var marker = Guid.NewGuid().ToString("D");

        using var response = await client.PostAsync($"/api/v1/diagnostics-probe/rollback/{marker}", null, Cancellation);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await pipeline.WaitForRecordAsync(item => OtlpJson.Attribute(Record(item), "Marker") == marker);
        Assert.Equal(0L, await QueryScalarAsync<long>("SELECT count(*) FROM identity.users"));
        Assert.Equal(0L, await QueryScalarAsync<long>(
            "SELECT count(*) FROM information_schema.tables WHERE table_name ILIKE '%diagnostic%' OR table_name ILIKE '%log_event%' OR table_name ILIKE '%telemetry%'"));
    }

    [Fact]
    public async Task Collector_sanitizes_again_what_another_producer_sends_unsanitized()
    {
        var marker = Guid.NewGuid().ToString("N");
        var nanos = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        const string Jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2lnbmF0dXJl";
        var payload = new
        {
            resourceLogs = new[]
            {
                new
                {
                    resource = new { attributes = new[] { new { key = "service.name", value = new { stringValue = "produtor-externo" } } } },
                    scopeLogs = new[]
                    {
                        new
                        {
                            logRecords = new[]
                            {
                                new
                                {
                                    timeUnixNano = nanos,
                                    body = new { stringValue = $"{marker} Bearer {Jwt} para cliente@example.com {{\"password\":\"hunter2json\"}} senha: senhatexto" },
                                    attributes = new[] { new { key = "Header", value = new { stringValue = "Basic dXNlcjpzZWNyZXQ=" } } },
                                },
                            },
                        },
                    },
                },
            },
        };
        using var http = new HttpClient();
        using var response = await http.PostAsJsonAsync(new Uri(new Uri(pipeline.OtlpEndpoint), "v1/logs"), payload, Cancellation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await pipeline.WaitForRecordAsync(item =>
            Record(item).GetProperty("body").GetProperty("stringValue").GetString()!.StartsWith(marker, StringComparison.Ordinal));
        var text = stored.ToString();
        Assert.DoesNotContain(Jwt, text, StringComparison.Ordinal);
        Assert.DoesNotContain("dXNlcjpzZWNyZXQ=", text, StringComparison.Ordinal);
        Assert.DoesNotContain("cliente@example.com", text, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2json", text, StringComparison.Ordinal);
        Assert.DoesNotContain("senhatexto", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unavailable_collector_keeps_requests_and_errors_intact_with_bounded_queues_and_independent_signal()
    {
        using var signal = new StringWriter();
        using var listener = new TelemetryFailureSignal(TextWriter.Synchronized(signal), TimeSpan.Zero);
        // Coletor que aceita a conexão e nunca responde: cada exportação espera o timeout e a fila enche.
        using var unresponsive = new TcpListener(IPAddress.Loopback, 0);
        unresponsive.Start();
        var connections = new ConcurrentBag<TcpClient>();
        using var stop = new CancellationTokenSource();
        var accepting = Task.Run(async () =>
        {
            try { while (true) { connections.Add(await unresponsive.AcceptTcpClientAsync(stop.Token)); } }
            catch (OperationCanceledException) { }
        }, Cancellation);
        var logs = new ConcurrentQueue<string>();
        var api = WithEndpoint($"http://127.0.0.1:{((IPEndPoint)unresponsive.LocalEndpoint).Port}/", logs);
        HttpResponseMessage response;
        TimeSpan logging;
        var elapsed = Stopwatch.StartNew();
        try
        {
            var logger = api.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Ouroboros.Saturation");
            for (var index = 0; index < 30_000; index++) { logger.LogWarning("Carga de diagnóstico {Index}", index); }
            logging = elapsed.Elapsed;
            using var client = api.CreateClient();
            response = await client.GetAsync("/api/v1/diagnostics-probe/fail", Cancellation);
        }
        finally
        {
            await api.DisposeAsync();
            elapsed.Stop();
            await stop.CancelAsync();
            await accepting;
            foreach (var connection in connections) { connection.Dispose(); }
        }

        Assert.True(logging < TimeSpan.FromSeconds(5), $"Registro bloqueou o chamador: {logging}.");
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(30), $"Encerramento sem limite: {elapsed.Elapsed}.");
        Assert.NotEmpty(connections);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        using (var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancellation)))
        { Assert.Equal(500, problem.RootElement.GetProperty("status").GetInt32()); }
        // O SDK emite o descarte agregado no encerramento do processador; a escrita do sinal pode terminar depois.
        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(Cancellation))
        {
            wait.CancelAfter(TimeSpan.FromSeconds(15));
            while (!signal.ToString().Contains("dropped", StringComparison.OrdinalIgnoreCase)) { await Task.Delay(100, wait.Token); }
        }
        var text = signal.ToString();
        Assert.Contains("ouroboros-observability", text, StringComparison.Ordinal);
        Assert.Contains("dropped", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(logs, entry => entry.Contains("OpenTelemetry", StringComparison.Ordinal));
    }

    private static JsonElement Record(JsonElement item) => item.GetProperty("record");

    private static string? TraceOf(JsonElement item) => Record(item).TryGetProperty("traceId", out var trace) ? trace.GetString() : null;

    private ApiFactory WithEndpoint(string endpoint, ConcurrentQueue<string>? logs = null) =>
        Api.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Observability:Otlp:Endpoint"] = endpoint }));
            if (logs is not null) { builder.ConfigureLogging(logging => logging.AddProvider(new ListLoggerProvider(logs))); }
        });
}

// Sonda registrada só nos testes: emite diagnósticos com dados sensíveis e reverte uma transação de negócio.
[ApiController]
[AllowAnonymous]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/diagnostics-probe")]
public sealed partial class DiagnosticsProbeController(ILogger<DiagnosticsProbeController> logger) : ControllerBase
{
    internal const string Marker = "diagnostics-probe-sensitive";
    internal const string Password = "S3nh@Forte!Diagnostico";
    internal const string Jwt = "eyJhbGciOiJSUzI1NiIsImtpZCI6InRlc3QifQ.eyJzdWIiOiIxMjM0NTY3ODkwIn0.c2lnbmF0dXJlLXRlc3QtdmFsdWU";
    internal const string Email = "cliente.secreto@example.com";
    internal const string Link = "http://localhost/confirm?token=private-test-token";
    internal static readonly string[] Secrets = [Password, Jwt, Email, "private-test-token"];

    [HttpGet("fail")]
    public IActionResult Fail()
    {
        LogSensitive(logger, Marker, Password, Email, Link);
        throw new InvalidOperationException($"Falha Npgsql em Host=db;Password={Password} com Bearer {Jwt}");
    }

    [HttpPost("rollback/{marker}")]
    public async Task<IActionResult> Rollback(string marker, CancellationToken cancellationToken)
    {
        var context = HttpContext.RequestServices.GetRequiredService<IdentityDbContext>();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        context.Users.Add(Ouroboros.Identity.Domain.Users.User.Create("rollback@example.com", "hash-de-teste", UserRole.Customer));
        await context.SaveChangesAsync(cancellationToken);
        LogInsideTransaction(logger, marker);
        await transaction.RollbackAsync(cancellationToken);
        return Problem(statusCode: StatusCodes.Status409Conflict, title: "Operação revertida.");
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sonda {Marker}: senha {Password}, destinatário {Email}, link {Link}")]
    private static partial void LogSensitive(ILogger logger, string marker, string password, string email, string link);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Diagnóstico dentro de transação revertida {Marker}")]
    private static partial void LogInsideTransaction(ILogger logger, string marker);
}
