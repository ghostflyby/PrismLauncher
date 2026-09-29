// SPDX-License-Identifier: GPL-3.0-only
// In-memory Microsoft.Extensions.Logging sink for asserting the diagnostics channel.
// FlintLog is process-global, so attach/detach must bracket the assertions (the suite runs
// with collections serialized, see xunit.runner.json).
using Microsoft.Extensions.Logging;

namespace FlintLauncher.Tests;

public sealed class TestLogSink : ILoggerProvider
{
    public sealed record Entry(LogLevel Level, string Category, string Message, Exception? Exception);

    private readonly object _lock = new();
    public List<Entry> Entries { get; } = [];

    public ILogger CreateLogger(string categoryName) => new Capturing(this, categoryName);

    public void Dispose() { }

    public bool Has(LogLevel level, string category, string messageContains)
    {
        lock (_lock)
        {
            return Entries.Any(e =>
                e.Level == level && e.Category == category && e.Message.Contains(messageContains));
        }
    }

    private sealed class Capturing(TestLogSink sink, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter)
        {
            lock (sink._lock)
            {
                sink.Entries.Add(new Entry(logLevel, category, formatter(state, exception), exception));
            }
        }
    }
}

public static class FlintLogTest
{
    private static ILoggerFactory? _factory;

    /// <summary>Point the global FlintLog facade at a fresh in-memory sink (Trace level).</summary>
    public static TestLogSink Attach()
    {
        var sink = new TestLogSink();
        _factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(sink);
        });
        Core.Diagnostics.FlintLog.Initialize(_factory);
        return sink;
    }

    public static void Detach()
    {
        Core.Diagnostics.FlintLog.Reset();
        _factory?.Dispose();
        _factory = null;
    }
}
