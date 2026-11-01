using Microsoft.Extensions.Logging;

namespace RecruIT.List.Tests;

/// <summary>An ILogger that remembers what was logged, for tests asserting that a failure left a trace.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<Entry> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter
    ) => Entries.Add(new Entry(logLevel, exception, formatter(state, exception)));

    public sealed record Entry(LogLevel Level, Exception? Exception, string Message);
}
