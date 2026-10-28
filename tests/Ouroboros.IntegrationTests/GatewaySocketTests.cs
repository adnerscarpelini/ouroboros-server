extern alias admingateway;
extern alias externalgateway;

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using admingateway::Ouroboros.AdminGateway.Api;
using externalgateway::Ouroboros.ExternalGateway.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ouroboros.IntegrationTests;

[Collection("Telemetry")]
public sealed class GatewaySocketTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Connection_headers_are_filtered_over_real_http_in_both_directions(bool admin)
    {
        Dictionary<string, string>? received = null;
        string? receivedBody = null;
        await using var scenario = await SocketScenario.CreateAsync(admin, async context =>
        {
            received = context.Request.Headers.ToDictionary(header => header.Key, header => header.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            using var reader = new StreamReader(context.Request.Body);
            receivedBody = await reader.ReadToEndAsync(context.RequestAborted);
            context.Response.Headers.Connection = "X-Hop-Response, content-LANGUAGE";
            context.Response.Headers["X-Hop-Response"] = "remove";
            context.Response.Headers["X-End-To-End"] = "kept";
            context.Response.Headers.ContentLanguage = "pt-BR";
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"ok\":true}", context.RequestAborted);
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, scenario.Path)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Connection", [" x-HOP-request, Content-Language ", "X-Forwarded-For, X-Forwarded-Proto"]);
        request.Headers.TryAddWithoutValidation("X-Hop-Request", "remove");
        request.Headers.TryAddWithoutValidation("X-End-To-End", "kept");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.99");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "forged");
        request.Content.Headers.ContentLanguage.Add("pt-BR");

        using var response = await scenario.Client.SendAsync(request, Cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(received);
        Assert.False(received.ContainsKey("X-Hop-Request"));
        Assert.False(received.ContainsKey("Content-Language"));
        Assert.Equal("kept", received["X-End-To-End"]);
        Assert.Equal("127.0.0.1", received["X-Forwarded-For"]);
        Assert.Equal("http", received["X-Forwarded-Proto"]);
        Assert.StartsWith("application/json", received["Content-Type"]);
        Assert.Equal("{}", receivedBody);
        Assert.False(response.Headers.Contains("X-Hop-Response"));
        Assert.False(response.Headers.Contains("Connection"));
        Assert.Empty(response.Content.Headers.ContentLanguage);
        Assert.Equal("kept", Assert.Single(response.Headers.GetValues("X-End-To-End")));
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("{\"ok\":true}", await response.Content.ReadAsStringAsync(Cancellation));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Downstream_body_failure_before_gateway_response_returns_502_or_504(bool admin, bool timeout)
    {
        await using var scenario = await SocketScenario.CreateAsync(admin, async context =>
        {
            context.Response.ContentLength = 1000;
            context.Response.ContentType = "text/plain";
            context.Response.Headers["X-Downstream-Only"] = "private-downstream-detail";
            // Cabeçalhos reais chegam ao gateway; o corpo ainda não iniciou a resposta dele ao cliente.
            await context.Response.StartAsync(context.RequestAborted);
            if (timeout) { await WaitForDisconnectAsync(context); }
            else { context.Abort(); }
        }, TimeSpan.FromSeconds(1));
        var started = Stopwatch.GetTimestamp();

        using var response = await scenario.Client.GetAsync(scenario.Path, Cancellation);

        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(6));
        Assert.Equal(timeout ? HttpStatusCode.GatewayTimeout : HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.False(response.Headers.Contains("X-Downstream-Only"));
        var body = await response.Content.ReadAsStringAsync(Cancellation);
        Assert.DoesNotContain("private-downstream-detail", body);
        Assert.DoesNotContain(scenario.DownstreamAddress, body);
        using var problem = JsonDocument.Parse(body);
        Assert.Equal((int)response.StatusCode, problem.RootElement.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(problem.RootElement.GetProperty("traceId").GetString()));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Partial_response_then_failure_is_interrupted_without_appending_problem(bool admin, bool timeout)
    {
        var releaseFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var scenario = await SocketScenario.CreateAsync(admin, async context =>
        {
            context.Response.ContentType = "text/plain";
            context.Response.ContentLength = 1000;
            await context.Response.WriteAsync("first", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            try { await releaseFailure.Task.WaitAsync(context.RequestAborted); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { return; }
            if (timeout) { await WaitForDisconnectAsync(context); }
            else { context.Abort(); }
        }, TimeSpan.FromSeconds(2));
        using var request = new HttpRequestMessage(HttpMethod.Get, scenario.Path);
        try
        {
            using var response = await scenario.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Cancellation);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/plain", response.Content.Headers.ContentType!.MediaType);
            Assert.Equal(1000, response.Content.Headers.ContentLength);
            await using var body = await response.Content.ReadAsStreamAsync(Cancellation);
            var first = new byte[5];
            await body.ReadExactlyAsync(first, Cancellation);
            Assert.Equal("first", Encoding.UTF8.GetString(first));
            releaseFailure.TrySetResult();
            using var remaining = new MemoryStream();
            await Assert.ThrowsAnyAsync<IOException>(() => body.CopyToAsync(remaining, Cancellation));
            Assert.Empty(remaining.ToArray());
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally { releaseFailure.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task First_chunk_reaches_client_before_downstream_completes(bool admin)
    {
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var scenario = await SocketScenario.CreateAsync(admin, async context =>
        {
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync("first", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            try { await releaseSecond.Task.WaitAsync(context.RequestAborted); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { return; }
            await context.Response.WriteAsync("second", context.RequestAborted);
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, scenario.Path);
        try
        {
            using var response = await scenario.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Cancellation);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await using var body = await response.Content.ReadAsStreamAsync(Cancellation);
            var first = new byte[5];
            await body.ReadExactlyAsync(first, Cancellation);
            Assert.Equal("first", Encoding.UTF8.GetString(first));
            Assert.False(releaseSecond.Task.IsCompleted);
            releaseSecond.TrySetResult();
            using var reader = new StreamReader(body);
            Assert.Equal("second", await reader.ReadToEndAsync(Cancellation));
        }
        finally { releaseSecond.TrySetResult(); }
    }

    private static async Task WaitForDisconnectAsync(HttpContext context)
    {
        try { await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    }

    private sealed class SocketScenario(WebApplication downstream, WebApplication gateway, bool admin) : IAsyncDisposable
    {
        internal string Path => admin ? "/api/v1/admin/identity/me" : "/api/v1/identity/keys";
        internal string DownstreamAddress => Assert.Single(downstream.Urls);
        internal HttpClient Client { get; } = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(Assert.Single(gateway.Urls)),
            Timeout = TimeSpan.FromSeconds(10),
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

        internal static async Task<SocketScenario> CreateAsync(bool admin, RequestDelegate handler, TimeSpan? timeout = null)
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0, listener => listener.Protocols = HttpProtocols.Http1));
            var downstream = builder.Build();
            downstream.Run(handler);
            try
            {
                await downstream.StartAsync(Cancellation);
                var project = admin ? "Ouroboros.AdminGateway.Api" : "Ouroboros.ExternalGateway.Api";
                string[] args = ["--environment", "Testing", "--contentRoot", RepositoryRoot.Resolve("src", "Gateways", project)];
                void Configure(WebApplicationBuilder gatewayBuilder) => gatewayBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Gateway:Urls:0"] = "http://127.0.0.1:0",
                    ["Gateway:AllowNonLoopback"] = "false",
                    ["Gateway:Downstream:IdentityBaseUrl"] = Assert.Single(downstream.Urls),
                    ["Gateway:Downstream:Timeout"] = (timeout ?? TimeSpan.FromSeconds(5)).ToString("c", CultureInfo.InvariantCulture),
                });
                var gateway = admin ? AdminGatewayHost.Build(args, Configure) : ExternalGatewayHost.Build(args, Configure);
                try
                {
                    if (admin) { AdminGatewayHost.UsePipeline(gateway); }
                    else { ExternalGatewayHost.UsePipeline(gateway); }
                    await gateway.StartAsync(Cancellation);
                    return new SocketScenario(downstream, gateway, admin);
                }
                catch { await gateway.DisposeAsync(); throw; }
            }
            catch { await downstream.DisposeAsync(); throw; }
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            try
            {
                try { await gateway.StopAsync(TimeSpan.FromSeconds(5)); }
                finally { await gateway.DisposeAsync(); }
            }
            finally
            {
                try { await downstream.StopAsync(TimeSpan.FromSeconds(5)); }
                finally { await downstream.DisposeAsync(); }
            }
        }
    }
}
