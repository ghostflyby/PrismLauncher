// SPDX-License-Identifier: GPL-3.0-only
// Source-generated log events for the diagnostics channel ([LoggerMessage] emits plain
// formatting code — no reflection-based template parsing, AOT-safe and allocation-light).
// Grouped by component; event ids are unique within each class. The points trace exactly
// what a launch investigation needs: the full java command line (upstream logs it via
// qDebug in LauncherPartLaunch.cpp:131), what was written to the child's stdin and when,
// gate arm/consume edges and per-step outcomes.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlintLauncher.Core.Diagnostics;

/// <summary>Diagnostic categories, mirroring the upstream Qt categories style
/// (launcher.task, launcher.task.net, ...). Resolved from the injected ILoggerFactory
/// through <see cref="Log.For"/> — the former static facade carried these same strings.</summary>
public static class LogCategories
{
    /// <summary>LoggedProcess: spawn command line, stdin writes, state, exit.</summary>
    public const string Process = "flint.process";

    /// <summary>LaunchTask step engine + LauncherPartLaunch gate events.</summary>
    public const string Launch = "flint.launch";

    /// <summary>LaunchController: account selection, launch outcome.</summary>
    public const string Controller = "flint.controller";

    /// <summary>FlintTaskRegistry: task lifecycle.</summary>
    public const string Tasks = "flint.tasks";

    /// <summary>Web request log.</summary>
    public const string Http = "flint.http";

    /// <summary>API auth: the S2 process-lifetime token announcement.</summary>
    public const string ApiAuth = "flint.apiauth";

    /// <summary>Migration (GLOBAL G2): the flint-legacy startup remount outcome.</summary>
    public const string Migration = "flint.migration";

    /// <summary>Startup assembly diagnostics (GLOBAL §2 step 2): the C5 INI import outcome
    /// and the mid-assembly crash report — everything the host logs after Build for steps
    /// the runtime constructor ran before any logger existed.</summary>
    public const string Startup = "flint.startup";
}

/// <summary>Per-instance resolution of a diagnostics logger from the injected
/// ILoggerFactory. Replaces the deleted FlintLog static facade: the factory travels through
/// constructor injection (LaunchServices/LaunchController/LoggedProcess/DI), so nothing here
/// is process-global — only the dispose semantics are kept 1:1: an unknown factory degrades
/// to silence and a disposed host's factory must degrade to silence, not throw.</summary>
public static class Log
{
    /// <summary>Logger for a category (see <see cref="LogCategories"/>). NullLogger for a
    /// null factory so silent-by-default Core stays usable as a plain library. Resolve per
    /// call site and never cache the result: a disposed host's factory must degrade to
    /// silence, not throw.</summary>
    public static ILogger For(ILoggerFactory? factory, string category)
    {
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
}

/// <summary>flint.process — LoggedProcess.</summary>
public static partial class ProcessLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
        Message = "Starting process {Program} with arguments: {Args}")]
    public static partial void Starting(this ILogger logger, string program, string args);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug,
        Message = "Stdin write: {CharCount} chars")]
    public static partial void StdinWrite(this ILogger logger, int charCount);

    [LoggerMessage(EventId = 3, Level = LogLevel.Trace,
        Message = "Stdin write preview: {Preview}")]
    public static partial void StdinWritePreview(this ILogger logger, string preview);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning,
        Message = "Stdin write failed, the child never saw these bytes: {Reason}")]
    public static partial void StdinWriteFailed(this ILogger logger, string reason);

    [LoggerMessage(EventId = 5, Level = LogLevel.Debug,
        Message = "State -> {State}")]
    public static partial void StateChanged(this ILogger logger, string state);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information,
        Message = "Process exited with code {ExitCode}")]
    public static partial void Exited(this ILogger logger, int exitCode);

    [LoggerMessage(EventId = 7, Level = LogLevel.Information,
        Message = "Process killed by user (aborted)")]
    public static partial void Killed(this ILogger logger);

    [LoggerMessage(EventId = 8, Level = LogLevel.Error,
        Message = "Process failed to start: {Reason}")]
    public static partial void FailedToStart(this ILogger logger, string reason);
}

/// <summary>flint.launch — LaunchTask step engine + LauncherPartLaunch gate events.</summary>
public static partial class LaunchLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
        Message = "Step {Index}/{Total} started: {StepName}")]
    public static partial void StepStarted(this ILogger logger, int index, int total, string stepName);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug,
        Message = "Step finished: {StepName} (success={Success}, reason={Reason})")]
    public static partial void StepFinished(this ILogger logger, string stepName, bool success, string reason);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information,
        Message = "ReadyForLaunch gate open at step {StepName}")]
    public static partial void ReadyForLaunch(this ILogger logger, string stepName);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information,
        Message = "Proceed routed to step {StepName}")]
    public static partial void ProceedRouted(this ILogger logger, string stepName);

    [LoggerMessage(EventId = 5, Level = LogLevel.Debug,
        Message = "Proceed ignored: engine state is {State}, not Waiting")]
    public static partial void ProceedIgnored(this ILogger logger, string state);

    [LoggerMessage(EventId = 6, Level = LogLevel.Debug,
        Message = "Progress-report gate open at step {StepName}")]
    public static partial void ProgressGate(this ILogger logger, string stepName);

    [LoggerMessage(EventId = 7, Level = LogLevel.Debug,
        Message = "Launch script piped to stdin ({CharCount} chars)")]
    public static partial void ScriptWritten(this ILogger logger, int charCount);

    [LoggerMessage(EventId = 8, Level = LogLevel.Information,
        Message = "'launch' written to game stdin")]
    public static partial void LaunchSignalSent(this ILogger logger);

    [LoggerMessage(EventId = 9, Level = LogLevel.Debug,
        Message = "Proceed skipped: gate not armed (already consumed or not reached)")]
    public static partial void GateNotArmed(this ILogger logger);
}

/// <summary>flint.controller — LaunchController.</summary>
public static partial class ControllerLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Launching instance {Instance} (mode {Mode}, account {Account}, target {Target})")]
    public static partial void LaunchStarted(this ILogger logger, string instance, string mode, string account, string target);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information,
        Message = "Launch finished: {Outcome}")]
    public static partial void LaunchFinished(this ILogger logger, string outcome);
}

/// <summary>flint.tasks — FlintTaskRegistry lifecycle.</summary>
public static partial class TasksLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
        Message = "Task {TaskId} ({Kind}) registered")]
    public static partial void Registered(this ILogger logger, string taskId, string kind);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug,
        Message = "Task {TaskId} finished ({State})")]
    public static partial void Finished(this ILogger logger, string taskId, string state);
}

/// <summary>flint.http — API request log (one line per request, health probes at Debug).</summary>
public static partial class HttpLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "http {Method} {Path} -> {StatusCode} in {ElapsedMs:0.0}ms")]
    public static partial void Request(this ILogger logger, string method, string path, int statusCode, double elapsedMs);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug,
        Message = "http {Method} {Path} -> {StatusCode} in {ElapsedMs:0.0}ms")]
    public static partial void Probe(this ILogger logger, string method, string path, int statusCode, double elapsedMs);
}
