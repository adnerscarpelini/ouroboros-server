using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Ouroboros.ArchitectureTests;

// Guarda os logs emitidos pelo host junto com o TraceId e o SpanId do escopo da requisição, para comparar
// com o traceId devolvido no ProblemDetails.
internal sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    public ConcurrentQueue<CapturedLog> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
    }

    private string? CurrentScopeValue(string name)
    {
        string? found = null;
        _scopes.ForEachScope(
            (scope, _) =>
            {
                if (scope is IEnumerable<KeyValuePair<string, object?>> values)
                {
                    foreach (var (key, value) in values)
                    {
                        if (key == name)
                        {
                            found = value?.ToString();
                        }
                    }
                }
            },
            (object?)null);
        return found;
    }

    private sealed class CapturingLogger(CapturingLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => provider._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            provider.Entries.Enqueue(new CapturedLog(category, logLevel, formatter(state, exception), exception,
                provider.CurrentScopeValue("TraceId"), provider.CurrentScopeValue("SpanId")));
    }
}

internal sealed record CapturedLog(string Category, LogLevel Level, string Message, Exception? Exception, string? TraceId, string? SpanId = null);
