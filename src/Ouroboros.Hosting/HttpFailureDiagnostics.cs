using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Ouroboros.Hosting;

internal sealed partial class HttpFailureDiagnostics(RequestDelegate next, ILogger<HttpFailureDiagnostics> logger)
{
    internal static readonly object FailureKey = new();
    internal static readonly object UnexpectedKey = new();

    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched";
        try { await next(context); }
        finally
        {
            var status = context.Response.StatusCode;
            if (context.RequestAborted.IsCancellationRequested)
            {
                LogCancelled(logger, route, status, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            else if (status >= 400)
            {
                var failure = context.Items[FailureKey] as HttpFailure;
                var unexpected = context.Items.ContainsKey(UnexpectedKey);
                var code = unexpected ? "unhandled-exception" : failure?.Code ?? $"http-{status}";
                var classification = unexpected ? "unexpected" : failure?.Classification ?? (status >= 500 ? "technical" : "http");
                // O resumo não leva a exceção; a ocorrência técnica pertence exclusivamente ao handler.
                LogResponse(logger, status, route, classification, code, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }
    }

    [LoggerMessage(EventId = 6701, Level = LogLevel.Warning, Message = "Resposta HTTP {Status} em {Route}: {Classification}, {Code}; duração {DurationMs} ms.")]
    private static partial void LogResponse(ILogger logger, int status, string route, string classification, string code, double durationMs);
    [LoggerMessage(EventId = 6702, Level = LogLevel.Information, Message = "Requisição cancelada pelo cliente em {Route}: {Status}, expected-cancellation; duração {DurationMs} ms.")]
    private static partial void LogCancelled(ILogger logger, string route, int status, double durationMs);
}

internal sealed record HttpFailure(string Classification, string Code);

internal sealed class HttpFailureResultFilter : IAlwaysRunResultFilter
{
    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is ObjectResult { Value: ProblemDetails problem }) { Capture(context.HttpContext, problem); }
    }
    public void OnResultExecuted(ResultExecutedContext context) { }

    internal static void Capture(HttpContext context, ProblemDetails problem)
    {
        var status = problem.Status ?? context.Response.StatusCode;
        if (status < 400) { return; }
        var validation = problem is HttpValidationProblemDetails;
        var code = validation ? "validation-failed" : $"http-{status}";
        // Só o fragmento de um tipo de erro pertencente à documentação pública é código de negócio.
        // O restante da URL e textos de validação nunca entram no diagnóstico.
        const string Prefix = "https://github.com/adnerscarpelini/ouroboros-server/blob/main/docs/errors/";
        if (!validation && problem.Type is { } type && type.StartsWith(Prefix, StringComparison.Ordinal)
            && Uri.TryCreate(type, UriKind.Absolute, out var uri) && uri.Fragment is { Length: > 1 and <= 65 } fragment
            && fragment.AsSpan(1).ToArray().All(character => char.IsAsciiLetterOrDigit(character) || character == '-'))
        { code = fragment[1..]; }
        context.Items[HttpFailureDiagnostics.FailureKey] = new HttpFailure(validation ? "validation" : status >= 500 ? "technical" : "business", code);
    }
}

internal sealed partial class SafeExceptionHandler(ILogger<SafeExceptionHandler> logger, IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        context.Items[HttpFailureDiagnostics.UnexpectedKey] = true;
        LogUnexpected(logger, exception);
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        var problem = new ProblemDetails { Status = 500, Title = "An error occurred while processing your request." };
        problem.Extensions["traceId"] = Activity.Current?.Id ?? context.TraceIdentifier;
        if (!await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = problem,
        }))
        {
            // O escritor padrão pode recusar Accept:text/plain. A fronteira ainda deve encerrar o erro
            // com o contrato genérico, sem relançar a exceção e acionar outro ponto de captura.
            await context.Response.WriteAsJsonAsync(problem, options: (System.Text.Json.JsonSerializerOptions?)null,
                contentType: "application/problem+json", cancellationToken: cancellationToken);
        }
        return true;
    }

    [LoggerMessage(EventId = 6700, Level = LogLevel.Error, Message = "Exceção inesperada na entrada HTTP: unhandled-exception.")]
    private static partial void LogUnexpected(ILogger logger, Exception exception);
}
