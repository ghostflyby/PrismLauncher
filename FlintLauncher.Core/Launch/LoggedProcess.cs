// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/LoggedProcess.{h,cpp}: a basic process wrapper with line-based logging
// support that hides some of the nasty bits.
//
// Deviations from the C++ (QProcess based) original:
//  - QProcess becomes System.Diagnostics.Process. The stdout/stderr pipes are drained by two
//    dedicated reader loops that decode bytes through a stateful decoder (so multi-byte
//    characters split across reads survive) and split on line feeds exactly like the C++
//    reprocess(): carriage returns are removed anywhere in the chunk, the text is split on
//    '\n' and the last (possibly partial) element becomes the leftover line that is prepended
//    to the next chunk. Unlike the C++, a leftover line still buffered when the process exits
//    is flushed as a final line (QProcess loses it).
//  - Output encoding: the C++ default is QStringConverter::System (UTF-8 on macOS/Linux, the
//    ANSI codepage on Windows); the port defaults to UTF-8 (a strict superset for the log
//    bytes Minecraft emits) and accepts an explicit encoding override.
//  - Exit classification: QProcess distinguishes NormalExit from CrashExit (death by signal).
//    .NET cannot observe signal deaths portably, so every non-killed exit reports
//    Finished with the exit code; callers already treat non-zero exit codes as failures
//    (LauncherPartLaunch / LaunchCommand), matching the user-visible behavior. Kill() reports
//    Aborted like the C++ (SIGKILL is approximated by Kill(entireProcessTree: true), needed
//    for wrapper commands that spawn children).
//  - State transitions: QProcess moves through Starting asynchronously; the port emits
//    Starting and (on a successful spawn) Running synchronously from Start(). The C++ warns
//    about out-of-order transitions; the port just ignores no-op transitions via changeState.
//  - setDetachable: the C++ uses it so the QProcess destructor does not reap the game process
//    when the launch step is destroyed; the port only records the flag (System.Diagnostics
//    Process objects do not kill children on finalize).
//  - diagnostics: the C++ only qDebug's a state anomaly (LoggedProcess.cpp:162); the port
//    additionally traces spawn command line, stdin writes, every state change and the exit
//    through the injected "flint.process" logger — these were the exact blind spots of the
//    first real-launch investigation.

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using FlintLauncher.Core.Diagnostics;
using FlintLauncher.Core.Instances;
using Microsoft.Extensions.Logging;

namespace FlintLauncher.Core.Launch;

/// <summary>Port of the LoggedProcess::State enum.</summary>
public enum LoggedProcessState : byte
{
    NotRunning,
    Starting,
    FailedToStart,
    Running,
    Finished,
    Crashed,
    Aborted,
}

public sealed class LoggedProcess : IDisposable
{
    private readonly Process _process = new();
    private readonly object _lock = new();
    private Decoder? errDecoder;
    private Decoder? outDecoder;
    private string leftoverLine = "";
    private string errLeftoverLine = "";
    private bool isAborting;
    private bool isDetachable;
    // volatile: terminal states are applied on a continuation thread and read by waiters
    // that observed the StateChanged/TCS edge; keep the field reads fresh
    private volatile LoggedProcessState state = LoggedProcessState.NotRunning;
    private volatile int exitCode;
    private bool started;
    private bool disposed;
    /// <summary>Signalled by OnTerminated AFTER the terminal state is applied, so waiters never
    /// observe a stale Running state (Process.WaitForExitAsync races the exit continuation).</summary>
    private TaskCompletionSource<LoggedProcessState>? terminatedTcs;

    /// <summary>The injected diagnostics factory (the launch pipeline passes the host's
    /// LaunchServices.LoggerFactory); null keeps the silent-by-default library behavior.</summary>
    private readonly ILoggerFactory? _loggerFactory;

    /// <summary>Diagnostics logger (flint.process category). Resolved per call from the
    /// injected factory — a disposed host's factory degrades to silence, never throws.
    /// Named DiagLog: the Log event above is the process output signal.</summary>
    private ILogger DiagLog => Diagnostics.Log.For(_loggerFactory, LogCategories.Process);

    public LoggedProcess(Encoding? outputEncoding = null, ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory;
        var encoding = outputEncoding ?? Encoding.UTF8;
        outDecoder = encoding.GetDecoder();
        errDecoder = encoding.GetDecoder();

        _process.StartInfo.UseShellExecute = false;
        _process.StartInfo.RedirectStandardOutput = true;
        _process.StartInfo.RedirectStandardError = true;
        _process.StartInfo.RedirectStandardInput = true;
        _process.StartInfo.CreateNoWindow = true;
        _process.EnableRaisingEvents = true;
    }

    /// <summary>Port of the log(QStringList, MessageLevel) signal.</summary>
    public event Action<List<string>, MessageLevel>? Log;

    /// <summary>Port of the stateChanged(LoggedProcess::State) signal.</summary>
    public event Action<LoggedProcessState>? StateChanged;

    public LoggedProcessState State => state;

    /// <summary>Port of QProcess::errorString() for the FailedToStart log line.</summary>
    public string ErrorString { get; private set; } = "";

    public int ExitCode => exitCode;

    /// <summary>Port of QProcess::processId(); 0 when not running.</summary>
    public int ProcessId
    {
        get
        {
            try
            {
                return started && !_process.HasExited ? _process.Id : 0;
            }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception)
            {
                return 0;
            }
        }
    }

    /// <summary>Port of QProcess::setWorkingDirectory.</summary>
    public void SetWorkingDirectory(string directory)
    {
        _process.StartInfo.WorkingDirectory = directory;
    }

    /// <summary>Port of QProcess::setProcessEnvironment (the instance ProcessEnvironment).</summary>
    public void SetProcessEnvironment(ProcessEnvironment environment)
    {
        var env = _process.StartInfo.Environment;
        env.Clear();
        foreach (var (key, value) in environment.Vars)
        {
            env[key] = value;
        }
    }

    public void SetDetachable(bool detachable) => isDetachable = detachable;

    /// <summary>Port of QProcess::start(program, args). Returns false when the process failed
    /// to start (the FailedToStart log line and state change are emitted like the C++
    /// errorOccurred slot).</summary>
    public bool Start(string program, IReadOnlyList<string> args)
    {
        var startInfo = _process.StartInfo;
        startInfo.FileName = program;
        startInfo.ArgumentList.Clear();
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }
        // full command line into the diagnostics channel, like the upstream qDebug in
        // LauncherPartLaunch.cpp:131 (session tokens travel via the stdin script, not args)
        var log = DiagLog;
        log.Starting(program, string.Join(' ', args));

        lock (_lock)
        {
            started = false;
            isAborting = false;
            exitCode = 0;
            leftoverLine = "";
            errLeftoverLine = "";
        }

        ChangeState(LoggedProcessState.Starting);
        try
        {
            _process.Start();
        }
        catch (Exception e) when (e is Win32Exception or IOException or InvalidOperationException)
        {
            ErrorString = e.Message;
            log.FailedToStart(ErrorString);
            Log?.Invoke([$"The process failed to start: {ErrorString}"], MessageLevel.Fatal);
            ChangeState(LoggedProcessState.FailedToStart);
            return false;
        }
        started = true;
        terminatedTcs = new TaskCompletionSource<LoggedProcessState>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Declare Running BEFORE arming the exit continuation: a process that exits instantly
        // would otherwise have OnTerminated apply Finished and the late Running transition
        // clobber the terminal state (Qt emits started() asynchronously, so it always lands
        // first upstream).
        ChangeState(LoggedProcessState.Running);

        var stdoutTask = PumpAsync(_process.StandardOutput.BaseStream, isStdErr: false);
        var stderrTask = PumpAsync(_process.StandardError.BaseStream, isStdErr: true);
        // NOTE: the raw process wait, not WaitForExitAsync() — that one now awaits the terminal
        // TCS which OnTerminated (scheduled from this very WhenAll) would set: circular wait.
        var exitTask = _process.WaitForExitAsync();

        _ = Task.WhenAll(stdoutTask, stderrTask, exitTask).ContinueWith(
            static (t, state) => ((LoggedProcess)state!).OnTerminated(),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return true;
    }

    /// <summary>Port of QProcess::write(): write to the child's stdin. The write (and any
    /// failure — the C++ QProcess drops failed writes silently too, but we need the failure
    /// visible: a dropped "launch" line leaves the game waiting on stdin forever).</summary>
    public void Write(string text)
    {
        var log = DiagLog;
        try
        {
            var stream = _process.StandardInput;
            stream.Write(text);
            stream.Flush();
            log.StdinWrite(text.Length);
            log.StdinWritePreview(Preview(text));
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // the process is gone; QProcess would queue the write and drop it silently too
            log.StdinWriteFailed(e.GetType().Name + ": " + e.Message);
        }
    }

    /// <summary>Single-line excerpt of a stdin write for the Trace preview.</summary>
    private static string Preview(string text)
    {
        var singleLine = text.Replace("\r", "").Replace("\n", "\\n");
        return singleLine.Length <= 200
            ? singleLine
            : singleLine[..200] + $"… ({text.Length} chars total)";
    }

    /// <summary>Port of the kill() slot: equivalent to kill -9.</summary>
    public void Kill()
    {
        isAborting = true;
        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // already dead
        }
    }

    /// <summary>Waits for the process to exit; completes with the final LoggedProcessState.</summary>
    public async Task<LoggedProcessState> WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        if (!started)
        {
            return state;
        }
        try
        {
            await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
        }
        // the process is gone, but OnTerminated applies the terminal state on its own
        // continuation — wait for it so callers always see Finished/Aborted here
        if (terminatedTcs is { } terminated)
        {
            return await terminated.Task.ConfigureAwait(false);
        }
        return state;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        if (isDetachable)
        {
            // LoggedProcess::~LoggedProcess: pretend the process is gone so nothing reaps it
            ChangeState(LoggedProcessState.NotRunning);
        }
        _process.Dispose();
    }

    // ------------------------------------------------------------------------ internals

    private async Task PumpAsync(Stream stream, bool isStdErr)
    {
        var buffer = new byte[8192];
        while (true)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
            {
                break;
            }
            if (read == 0)
            {
                break;
            }

            var decoder = isStdErr ? errDecoder : outDecoder;
            var chars = new char[decoder!.GetCharCount(buffer, 0, read)];
            decoder.GetChars(buffer, 0, read, chars, 0);
            var str = new string(chars);

            lock (_lock)
            {
                var existing = isStdErr ? errLeftoverLine : this.leftoverLine;
                if (existing.Length != 0)
                {
                    str = existing + str;
                    if (isStdErr)
                    {
                        errLeftoverLine = "";
                    }
                    else
                    {
                        this.leftoverLine = "";
                    }
                }

                // reprocess(): drop carriage returns, split on line feed, keep the leftover
                str = str.Replace("\r", "");
                var lines = str.Split('\n');
                var leftoverLine = lines[^1];
                if (isStdErr)
                {
                    errLeftoverLine = leftoverLine;
                }
                else
                {
                    this.leftoverLine = leftoverLine;
                }

                var complete = lines[..^1];
                if (complete.Length != 0)
                {
                    Log?.Invoke([.. complete], isStdErr ? MessageLevel.StdErr : MessageLevel.StdOut);
                }
            }
        }

        // flush the final line if the process ended without a trailing newline (upstream QProcess
        // loses it; keeping it is strictly better and keeps "echo -n x" testable)
        string finalLine;
        lock (_lock)
        {
            finalLine = isStdErr ? errLeftoverLine : leftoverLine;
            if (isStdErr)
            {
                errLeftoverLine = "";
            }
            else
            {
                leftoverLine = "";
            }
        }
        if (finalLine.Length != 0)
        {
            Log?.Invoke([finalLine], isStdErr ? MessageLevel.StdErr : MessageLevel.StdOut);
        }
    }

    private void OnTerminated()
    {
        if (!started)
        {
            return;
        }
        int exitCode;
        try
        {
            exitCode = _process.ExitCode;
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
            return;
        }
        lock (_lock)
        {
            if (this.exitCode == 0)
            {
                this.exitCode = exitCode;
            }
        }

        // based on state, send signals
        if (!isAborting)
        {
            DiagLog.Exited(exitCode);
            Log?.Invoke([$"Process exited with code {exitCode}."], MessageLevel.Launcher);
            ChangeState(LoggedProcessState.Finished);
        }
        else
        {
            DiagLog.Killed();
            Log?.Invoke(["Process was killed by user."], MessageLevel.Error);
            ChangeState(LoggedProcessState.Aborted);
        }
        terminatedTcs?.TrySetResult(state);
    }

    private void ChangeState(LoggedProcessState state)
    {
        if (state == this.state)
        {
            return;
        }
        this.state = state;
        DiagLog.StateChanged(state.ToString());
        StateChanged?.Invoke(state);
    }
}
