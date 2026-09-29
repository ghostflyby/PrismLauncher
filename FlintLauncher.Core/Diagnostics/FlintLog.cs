// SPDX-License-Identifier: GPL-3.0-only
// Application-level diagnostics logging — port of the upstream appDebugOutput channel
// (Application.cpp:236-264 installs a Qt message handler fanning qDebug/qWarning out to a
// rolling log file, stderr and the GUI log model). The port keeps the sinks and levels in
// the host (console provider + RollingFileLoggerProvider), and Core reaches them through
// this static facade: Core services are constructed with `new` (no DI), so the host hands
// its ILoggerFactory over once at startup — the same shape as the other host hooks
// (LaunchTask.XmlParsingEnabled, LauncherPartLaunch.CloseAfterLaunchHook).
// The per-launch console channel (LogModel) stays strictly separate, like the C++ two
// channels (LaunchTask::getLogModel vs APPLICATION->logModel); the deliberate overlap is
// the few gate events LauncherPartLaunch mirrors into the launch console for API clients.
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlintLauncher.Core.Diagnostics;

public static class FlintLog
{
    private static volatile ILoggerFactory? _factory;

    /// <summary>Hand the host's LoggerFactory over (sinks and levels are the host's decision).
    /// Call <see cref="Reset"/> afterwards in tests.</summary>
    public static void Initialize(ILoggerFactory factory)
    {
        _factory = factory;
    }

    /// <summary>Back to NullLogger (test teardown, disposed hosts).</summary>
    public static void Reset()
    {
        _factory = null;
    }

    /// <summary>Logger for a category (see <see cref="Categories"/>). NullLogger until the
    /// host initializes, so the silent-by-default Core stays usable as a plain library.
    /// Resolve per call site and never cache the result: the factory can be swapped
    /// (Initialize/Reset) and a disposed host's factory must degrade to silence, not throw.</summary>
    public static ILogger For(string category)
    {
        var factory = _factory;
        if (factory is null)
        {
            return NullLogger.Instance;
        }
        try
        {
            return factory.CreateLogger(category);
        }
        catch (ObjectDisposedException)
        {
            return NullLogger.Instance;
        }
    }

    /// <summary>Diagnostic categories, mirroring the upstream Qt categories style
    /// (launcher.task, launcher.task.net, ...).</summary>
    public static class Categories
    {
        /// <summary>LoggedProcess: spawn command line, stdin writes, state, exit.</summary>
        public const string Process = "flint.process";
        /// <summary>LaunchTask step engine: step transitions and proceed gates.</summary>
        public const string Launch = "flint.launch";
        /// <summary>LaunchController: account selection, launch outcome.</summary>
        public const string Controller = "flint.controller";
        /// <summary>FlintTaskRegistry: task lifecycle.</summary>
        public const string Tasks = "flint.tasks";
        /// <summary>Web request log.</summary>
        public const string Http = "flint.http";
    }
}
