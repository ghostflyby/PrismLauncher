// SPDX-License-Identifier: GPL-3.0-only
// Port of the rolling log file sink of Application::init (Application.cpp:513-528): at
// every process start the previous generations shift down (Flint-3.log -> Flint-4.log ...
// Flint-0.log -> Flint-1.log) and a fresh Flint-0.log is opened — one generation per run,
// <Generations> kept. Every line is flushed (the C++ flushes per message too). An
// unwritable directory must not take the launcher down: the C++ shows a fatal dialog, the
// port degrades to console-only logging.
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace FlintLauncher.Web.Logging;

public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    public const int Generations = 5;

    private readonly StreamWriter? _writer;
    private readonly object _lock = new();

    public RollingFileLoggerProvider(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            for (var generation = Generations - 2; generation >= 0; generation--)
            {
                var source = Path.Combine(directory, FileName(generation));
                if (!File.Exists(source))
                {
                    continue;
                }
                File.Move(source, Path.Combine(directory, FileName(generation + 1)), overwrite: true);
            }
            _writer = new StreamWriter(
                File.Open(Path.Combine(directory, FileName(0)), FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true,
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                  or System.Security.SecurityException or NotSupportedException)
        {
            _writer = null;
        }
    }

    public ILogger CreateLogger(string categoryName) => new Writer(this, categoryName);

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
        }
    }

    private static string FileName(int generation) => $"Flint-{generation}.log";

    private sealed class Writer(RollingFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter)
        {
            var writer = provider._writer;
            if (writer is null || !IsEnabled(logLevel))
            {
                return;
            }
            var line = $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} " +
                       $"[{LevelName(logLevel)}] [{category}] {formatter(state, exception)}";
            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }
            lock (provider._lock)
            {
                writer.WriteLine(line);
            }
        }

        private static string LevelName(LogLevel level) => level switch
        {
            LogLevel.Trace => "trace",
            LogLevel.Debug => "debug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "error",
            LogLevel.Critical => "crit",
            _ => "none",
        };
    }
}
