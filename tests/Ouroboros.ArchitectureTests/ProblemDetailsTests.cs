using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Ouroboros.ArchitectureTests;

public sealed class ProblemDetailsTests(OuroborosApiFactory factory) : IClassFixture<OuroborosApiFactory>
{
    private const string ProblemJson = "application/problem+json";

    [Fact]
    public async Task Unexpected_error_returns_500_problem_without_internal_details()
    {
        var logs = new CapturingLoggerProvider();
        using var client = ProbeHost.CreateClient(factory, new ProbeCallCounter(), logs);

        var response = await client.GetAsync("/api/v1/problem-probe/unexpected", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(ProblemJson, response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain(ProblemProbeController.InternalDetail, body);
        Assert.DoesNotContain("InvalidOperationException", body);
        Assert.DoesNotContain("   at ", body);

        var traceId = TraceIdOf(body);
        var errorLog = Assert.Single(logs.Entries, entry => entry.Level == LogLevel.Error && entry.Exception is not null);
        Assert.Equal(ProblemProbeController.InternalDetail, errorLog.Exception!.Message);
        Assert.NotNull(errorLog.TraceId);
        Assert.Contains(errorLog.TraceId, traceId);
    }

    [Fact]
    public async Task Invalid_input_returns_400_with_errors_per_field()
    {
        using var client = ProbeHost.CreateClient(factory, new ProbeCallCounter());

        var response = await client.PostAsJsonAsync(
            "/api/v1/problem-probe/validation",
            new { Email = "nao-e-email" },
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ProblemJson, response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(body);
        Assert.True(document.RootElement.GetProperty("errors").TryGetProperty("Email", out var emailErrors));
        Assert.NotEqual(0, emailErrors.GetArrayLength());
        Assert.False(string.IsNullOrEmpty(TraceIdOf(body)));
    }

    [Fact]
    public async Task Business_failure_keeps_its_documented_type()
    {
        using var client = ProbeHost.CreateClient(factory, new ProbeCallCounter());

        var response = await client.GetAsync("/api/v1/problem-probe/business", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ProblemJson, response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(body);
        Assert.Equal(ProblemProbeController.BusinessProblemType, document.RootElement.GetProperty("type").GetString());
        Assert.False(string.IsNullOrEmpty(TraceIdOf(body)));
    }

    [Fact]
    public async Task Unknown_route_returns_problem_details()
    {
        using var client = ProbeHost.CreateClient(factory, new ProbeCallCounter());

        var response = await client.GetAsync("/api/v9/problem-probe/business", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ProblemJson, response.Content.Headers.ContentType?.MediaType);
        Assert.False(string.IsNullOrEmpty(TraceIdOf(body)));
    }

    private static string TraceIdOf(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("traceId").GetString()!;
    }
}
