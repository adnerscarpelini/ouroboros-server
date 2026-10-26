using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Ouroboros.IntegrationTests;

// Guarda cada log com o contexto W3C ativo no momento da chamada, como faz o console JSON.
internal sealed class TraceCapturingLogger<T> : ILogger<T>
{
    internal ConcurrentQueue<(string Message, string? TraceId, string? SpanId)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Enqueue((formatter(state, exception), Activity.Current?.TraceId.ToHexString(), Activity.Current?.SpanId.ToHexString()));
}
