using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Ouroboros.IntegrationTests;

// Guarda o texto formatado de cada log (com a exceção, se houver), para verificar que segredos não vazam.
internal sealed class ListLoggerProvider(ConcurrentQueue<string> entries) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new ListLogger(entries);

    public void Dispose()
    {
    }

    private sealed class ListLogger(ConcurrentQueue<string> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue(formatter(state, exception) + exception);
    }
}
