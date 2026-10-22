using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Ouroboros.ArchitectureTests;

public sealed class ApiVersioningTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Declared_version_responds_and_reports_supported_versions()
    {
        using var client = ProbeHost.CreateClient(factory, new ProbeCallCounter());

        var response = await client.GetAsync("/api/v1/probe", TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.Contains("1.0", HeaderValues(response, "api-supported-versions"));
    }

    [Fact]
    public async Task Undeclared_version_is_rejected_without_running_the_action()
    {
        var counter = new ProbeCallCounter();
        using var client = ProbeHost.CreateClient(factory, counter);

        var response = await client.GetAsync("/api/v9/versioned-probe", TestContext.Current.CancellationToken);

        Assert.False(response.IsSuccessStatusCode, $"Esperado erro, obtido {(int)response.StatusCode}.");
        Assert.Equal(0, counter.Calls);
    }

    [Fact]
    public async Task Deprecated_version_still_responds_and_reports_deprecation()
    {
        var counter = new ProbeCallCounter();
        using var client = ProbeHost.CreateClient(factory, counter);

        var response = await client.GetAsync("/api/v1/versioned-probe", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, counter.Calls);
        Assert.Contains("1.0", HeaderValues(response, "api-deprecated-versions"));
        Assert.Contains("2.0", HeaderValues(response, "api-supported-versions"));
    }

    [Fact]
    public async Task Request_without_version_segment_does_not_reach_the_action()
    {
        var counter = new ProbeCallCounter();
        using var client = ProbeHost.CreateClient(factory, counter);

        var response = await client.GetAsync("/api/versioned-probe", TestContext.Current.CancellationToken);

        Assert.False(response.IsSuccessStatusCode, $"Esperado erro, obtido {(int)response.StatusCode}.");
        Assert.Equal(0, counter.Calls);
    }

    private static string HeaderValues(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : "";
}
