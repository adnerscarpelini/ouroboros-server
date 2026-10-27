using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ouroboros.Hosting.Gateway;

public interface IGatewayDownstreamClientFactory
{
    HttpMessageInvoker CreateInvoker();
}

internal sealed class DefaultGatewayDownstreamClientFactory : IGatewayDownstreamClientFactory, IDisposable
{
    private readonly HttpMessageInvoker _invoker = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(3),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    }, disposeHandler: true);

    public HttpMessageInvoker CreateInvoker() => _invoker;

    public void Dispose() => _invoker.Dispose();
}

internal sealed partial class GatewayForwarder(
    GatewayAudienceDescriptor audienceDescriptor,
    IOptions<GatewayOptions> options,
    IGatewayDownstreamClientFactory clientFactory,
    IProblemDetailsService problemDetails,
    ILogger<GatewayForwarder> logger)
{
    private const string ExternalIdentityPrefix = "/api/v1/identity";
    private const string AdminIdentityPrefix = "/api/v1/admin/identity";
    private readonly GatewayAudience _audience = audienceDescriptor.Audience;

    private static readonly HashSet<string> HopByHopAndSpoofableHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "Proxy-Connection",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade",
        "Host",
        "Forwarded",
        "X-Forwarded-For",
        "X-Forwarded-Host",
        "X-Forwarded-Proto",
        "X-Forwarded-Prefix",
        "X-Original-For",
        "X-Real-IP",
        "X-Test-Listener",
    };

    private static readonly HashSet<string> ExcludedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade",
        "Server",
    };

    public bool MatchesAudienceRoute(PathString path)
    {
        var prefix = _audience == GatewayAudience.Admin ? AdminIdentityPrefix : ExternalIdentityPrefix;
        if (!path.HasValue)
        {
            return false;
        }

        var value = path.Value!;
        return value.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || value.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)
            || (_audience == GatewayAudience.Admin && (value.Equals("/api/v1/admin/settings/parameters", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("/api/v1/admin/settings/parameters/", StringComparison.OrdinalIgnoreCase)));
    }

    public async Task ForwardAsync(HttpContext context)
    {
        var settings = options.Value;
        var baseUri = new Uri(settings.Downstream.IdentityBaseUrl, UriKind.Absolute);
        var targetUri = new Uri(baseUri, context.Request.PathBase.Add(context.Request.Path).Value + context.Request.QueryString.Value);

        using var outgoing = BuildOutgoingRequest(context, targetUri);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeoutCts.CancelAfter(settings.Downstream.Timeout);

        try
        {
            using var downstreamResponse = await clientFactory.CreateInvoker().SendAsync(outgoing, timeoutCts.Token);
            await CopyDownstreamResponseAsync(context, downstreamResponse, timeoutCts.Token);
        }
        catch (Exception exception) when (context.RequestAborted.IsCancellationRequested
            && exception is OperationCanceledException or HttpRequestException or IOException)
        {
            context.Abort();
            return;
        }
        catch (OperationCanceledException exception)
        {
            LogDownstreamTimeout(logger, _audience.ToString(), context.Request.Path, exception);
            await WriteGatewayProblemAsync(
                context,
                StatusCodes.Status504GatewayTimeout,
                "https://tools.ietf.org/html/rfc9110#section-15.6.5",
                "Tempo limite excedido ao contatar o serviço.");
            return;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            LogDownstreamUnavailable(logger, _audience.ToString(), context.Request.Path, exception);
            await WriteGatewayProblemAsync(
                context,
                StatusCodes.Status502BadGateway,
                "https://tools.ietf.org/html/rfc9110#section-15.6.3",
                "Serviço temporariamente indisponível.");
            return;
        }
    }

    private static HttpRequestMessage BuildOutgoingRequest(HttpContext context, Uri targetUri)
    {
        var request = context.Request;
        var outgoing = new HttpRequestMessage(new HttpMethod(request.Method), targetUri)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };

        if (HasRequestBody(request))
        {
            outgoing.Content = new StreamContent(request.Body);
        }

        var connectionHeaders = ConnectionHeaders(request.Headers.Connection.Select(value => value ?? string.Empty));
        foreach (var header in request.Headers)
        {
            if (HopByHopAndSpoofableHeaders.Contains(header.Key) || connectionHeaders.Contains(header.Key))
            {
                continue;
            }

            if (string.Equals(header.Key, "traceparent", StringComparison.OrdinalIgnoreCase)
                || string.Equals(header.Key, "tracestate", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var values = header.Value.ToArray();
            if (!outgoing.Headers.TryAddWithoutValidation(header.Key, values))
            {
                outgoing.Content?.Headers.TryAddWithoutValidation(header.Key, values);
            }
        }

        // Define X-Forwarded-For exclusivamente a partir do IP real da conexão vista pelo gateway,
        // impedindo falsificação ou concatenação de cabeçalhos vindos de clientes não confiáveis.
        if (context.Connection.RemoteIpAddress is { } clientIp)
        {
            var normalizedIp = clientIp.IsIPv4MappedToIPv6 ? clientIp.MapToIPv4().ToString() : clientIp.ToString();
            outgoing.Headers.TryAddWithoutValidation("X-Forwarded-For", normalizedIp);
        }

        if (!string.IsNullOrWhiteSpace(request.Scheme))
        {
            outgoing.Headers.TryAddWithoutValidation("X-Forwarded-Proto", request.Scheme);
        }

        if (Activity.Current is { IdFormat: ActivityIdFormat.W3C, Id: { Length: > 0 } traceparent } activity)
        {
            outgoing.Headers.TryAddWithoutValidation("traceparent", traceparent);
            if (!string.IsNullOrWhiteSpace(activity.TraceStateString))
            {
                outgoing.Headers.TryAddWithoutValidation("tracestate", activity.TraceStateString);
            }
        }

        return outgoing;
    }

    private static bool HasRequestBody(HttpRequest request) =>
        request.ContentLength > 0
        || request.Headers.ContainsKey("Transfer-Encoding")
        || (!HttpMethods.IsGet(request.Method)
            && !HttpMethods.IsHead(request.Method)
            && !HttpMethods.IsDelete(request.Method)
            && !HttpMethods.IsTrace(request.Method)
            && !HttpMethods.IsOptions(request.Method)
            && request.ContentType is not null);

    private static async Task CopyDownstreamResponseAsync(HttpContext context, HttpResponseMessage downstream, CancellationToken cancellationToken)
    {
        context.Response.StatusCode = (int)downstream.StatusCode;
        var connectionHeaders = ConnectionHeaders(downstream.Headers.Connection);

        foreach (var header in downstream.Headers)
        {
            if (!ExcludedResponseHeaders.Contains(header.Key) && !connectionHeaders.Contains(header.Key))
            {
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }
        }

        foreach (var header in downstream.Content.Headers)
        {
            if (!ExcludedResponseHeaders.Contains(header.Key) && !connectionHeaders.Contains(header.Key))
            {
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }
        }

        context.Response.Headers.Remove("transfer-encoding");

        if (!HttpMethods.IsHead(context.Request.Method) && downstream.StatusCode != HttpStatusCode.NoContent && downstream.StatusCode != HttpStatusCode.NotModified)
        {
            await downstream.Content.CopyToAsync(context.Response.Body, cancellationToken);
        }
    }

    private static HashSet<string> ConnectionHeaders(IEnumerable<string> values) => values
        .SelectMany(value => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private async Task WriteGatewayProblemAsync(HttpContext context, int statusCode, string type, string title)
    {
        if (context.Response.HasStarted)
        {
            // Status e corpo já enviados não podem ser substituídos por ProblemDetails.
            context.Abort();
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        await problemDetails.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails
            {
                Type = type,
                Title = title,
                Status = statusCode,
            },
        });
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gateway ({Audience}) excedeu o tempo limite ao encaminhar {Path}.")]
    private static partial void LogDownstreamTimeout(ILogger logger, string audience, string path, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gateway ({Audience}) não conseguiu contatar o serviço de destino ao encaminhar {Path}.")]
    private static partial void LogDownstreamUnavailable(ILogger logger, string audience, string path, Exception exception);
}
