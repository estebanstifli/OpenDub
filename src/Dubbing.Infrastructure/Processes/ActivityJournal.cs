using Microsoft.Extensions.Logging;

namespace Dubbing.Infrastructure.Processes;

public static class ActivityJournal
{
    public static event Action<string>? Entry;
    public static void Write(string text) => Entry?.Invoke(text);
}

public sealed class ActivityLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new JournalLogger(categoryName);
    public void Dispose() { }
    private sealed class JournalLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (IsEnabled(level)) ActivityJournal.Write($"{level} · {category.Split('.').Last()}: {formatter(state, exception)}" + (exception is null ? "" : "\n" + exception)); }
    }
}
