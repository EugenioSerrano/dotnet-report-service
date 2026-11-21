using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ReportService.Testing;

/// <summary>
/// A logger provider that keeps every rendered log line in memory, so a
/// hermetic test can assert on what the code under test logged.
/// </summary>
public sealed class LogCapture : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    /// <summary>The captured lines, as <c>Level: Category: message</c>, in the order they were written.</summary>
    public IReadOnlyList<string> Lines => [.. _lines];

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _lines);

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (IsEnabled(logLevel))
            {
                lines.Enqueue($"{logLevel}: {category}: {formatter(state, exception)}");
            }
        }
    }
}
