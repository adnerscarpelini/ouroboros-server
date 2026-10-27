extern alias admingateway;
extern alias externalgateway;

using System.Net;
using System.Text;
using System.Text.Json;
using admingateway::Ouroboros.AdminGateway.Api;
using externalgateway::Ouroboros.ExternalGateway.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Ouroboros.Hosting.Gateway;

namespace Ouroboros.ArchitectureTests;

public sealed class GatewayResponseTransferTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Body_failure_before_response_starts_returns_gateway_problem(bool admin, bool timeout)
    {
        using var downstream = new StubFactory(new ScriptedContent(async (_, token) =>
        {
            if (timeout) { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            throw new IOException("private-downstream-body-detail");
        }));
        var logs = new CapturingLoggerProvider();
        await using var external = CreateGateway<ExternalGatewayEntryPoint>(downstream, logs);
        await using var administrative = CreateGateway<AdminGatewayEntryPoint>(downstream, logs);
        using var client = admin ? administrative.CreateClient() : external.CreateClient();

        using var response = await client.GetAsync(Path(admin), Cancellation);

        Assert.Equal(timeout ? HttpStatusCode.GatewayTimeout : HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.False(response.Headers.Contains("X-Downstream-Only"));
        Assert.Empty(response.Content.Headers.ContentLanguage);
        var body = await response.Content.ReadAsStringAsync(Cancellation);
        Assert.DoesNotContain("private-downstream-body-detail", body);
        using var problem = JsonDocument.Parse(body);
        Assert.Equal((int)response.StatusCode, problem.RootElement.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(problem.RootElement.GetProperty("traceId").GetString()));
        Assert.DoesNotContain(logs.Entries, entry => entry.Message.Contains("unhandled-exception", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Body_failure_after_response_starts_aborts_without_appending_problem(bool admin, bool timeout)
    {
        var releaseFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var downstream = new StubFactory(new ScriptedContent(async (stream, token) =>
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes("first"), token);
            await stream.FlushAsync(token);
            await releaseFailure.Task.WaitAsync(token);
            if (timeout) { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            throw new IOException("private-downstream-body-detail");
        }));
        var logs = new CapturingLoggerProvider();
        await using var external = CreateGateway<ExternalGatewayEntryPoint>(downstream, logs);
        await using var administrative = CreateGateway<AdminGatewayEntryPoint>(downstream, logs);
        using var client = admin ? administrative.CreateClient() : external.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, Path(admin));
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Cancellation);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/plain", response.Content.Headers.ContentType!.MediaType);
            await using var body = await response.Content.ReadAsStreamAsync(Cancellation);
            var first = new byte[5];
            await body.ReadExactlyAsync(first, Cancellation);
            Assert.Equal("first", Encoding.UTF8.GetString(first));
            releaseFailure.TrySetResult();
            await Assert.ThrowsAnyAsync<IOException>(() => body.CopyToAsync(new MemoryStream(), Cancellation));
            Assert.DoesNotContain(logs.Entries, entry => entry.Message.Contains("unhandled-exception", StringComparison.Ordinal));
        }
        finally { releaseFailure.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Client_cancellation_during_body_is_not_an_unexpected_failure(bool admin)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var downstream = new StubFactory(new ScriptedContent(async (_, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { cancelled.TrySetResult(); }
        }));
        var logs = new CapturingLoggerProvider();
        await using var external = CreateGateway<ExternalGatewayEntryPoint>(downstream, logs, "00:00:10");
        await using var administrative = CreateGateway<AdminGatewayEntryPoint>(downstream, logs, "00:00:10");
        using var client = admin ? administrative.CreateClient() : external.CreateClient();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        var pending = client.GetAsync(Path(admin), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Cancellation);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { using var response = await pending; });
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), Cancellation);
        Assert.DoesNotContain(logs.Entries, entry => entry.Message.Contains("unhandled-exception", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Successful_body_is_forwarded_without_waiting_for_the_entire_content(bool admin)
    {
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var downstream = new StubFactory(new ScriptedContent(async (stream, token) =>
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes("first"), token);
            await stream.FlushAsync(token);
            await releaseSecond.Task.WaitAsync(token);
            await stream.WriteAsync(Encoding.UTF8.GetBytes("second"), token);
        }));
        var logs = new CapturingLoggerProvider();
        await using var external = CreateGateway<ExternalGatewayEntryPoint>(downstream, logs, "00:00:10");
        await using var administrative = CreateGateway<AdminGatewayEntryPoint>(downstream, logs, "00:00:10");
        using var client = admin ? administrative.CreateClient() : external.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, Path(admin));
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Cancellation);
            await using var body = await response.Content.ReadAsStreamAsync(Cancellation);
            var first = new byte[5];
            await body.ReadExactlyAsync(first, Cancellation);
            Assert.Equal("first", Encoding.UTF8.GetString(first));
            releaseSecond.TrySetResult();
            using var rest = new StreamReader(body);
            Assert.Equal("second", await rest.ReadToEndAsync(Cancellation));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("kept", Assert.Single(response.Headers.GetValues("X-Downstream-Only")));
        }
        finally { releaseSecond.TrySetResult(); }
    }

    private static string Path(bool admin) => admin ? "/api/v1/admin/identity/me" : "/api/v1/identity/keys";

    private static WebApplicationFactory<TEntryPoint> CreateGateway<TEntryPoint>(StubFactory downstream, CapturingLoggerProvider logs, string timeout = "00:00:01")
        where TEntryPoint : class => new WebApplicationFactory<TEntryPoint>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Downstream:Timeout"] = timeout,
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IGatewayDownstreamClientFactory>();
                services.AddSingleton<IGatewayDownstreamClientFactory>(downstream);
                services.AddSingleton<ILoggerProvider>(logs);
            });
        });

    private sealed class ScriptedContent(Func<Stream, CancellationToken, Task> write) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => write(stream, CancellationToken.None);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) => write(stream, cancellationToken);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class StubFactory(HttpContent content) : IGatewayDownstreamClientFactory, IDisposable
    {
        private readonly HttpMessageInvoker _invoker = new(new StubHandler(content));
        public HttpMessageInvoker CreateInvoker() => _invoker;
        public void Dispose() => _invoker.Dispose();

        private sealed class StubHandler(HttpContent content) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
                response.Headers.TryAddWithoutValidation("X-Downstream-Only", "kept");
                response.Content.Headers.ContentType = new("text/plain");
                response.Content.Headers.ContentLanguage.Add("pt-BR");
                return Task.FromResult(response);
            }
        }
    }
}
