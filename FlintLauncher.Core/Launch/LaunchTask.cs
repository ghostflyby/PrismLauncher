// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/launch/LaunchTask.{h,cpp}: the sequential step engine that drives one
// instance launch.
//
// The C++ engine is purely signal driven: executeTask() starts the first step and each step's
// finished() signal triggers onStepFinished(), which advances (or finalizes) the chain. The
// Qt "run the game now" gate works through the readyForLaunch() signal reaching the UI, which
// calls back proceed(). The C# port keeps the exact state machine and event flow, with these
// documented deviations:
//  - StartAsync() is awaitable: ExecuteAsync() parks on a TaskCompletionSource that the
//    terminal emissions (succeeded/failed/aborted) complete, so `await launchTask.StartAsync()`
//    covers the whole launch including the "game is running" phase (the C++ LaunchTask also
//    only emits finished() once the LauncherPartLaunch step sees the game exit).
//  - proceed() gating: the ReadyForLaunch event plus the Proceed() method map 1:1 to the C++
//    readyForLaunch()/proceed() pair. AbortProceed() is the C#-side shorthand for aborting
//    while the engine is Waiting (the C++ UI calls abort() for this). One deliberate
//    deviation: Proceed() restores LaunchState.Running after the step consumes the gate —
//    the C++ stays Waiting forever (its GUI never re-reads the state), while this API
//    reports the state to clients, who would otherwise see "waiting" during the whole game
//    session with no signal that the gate was already opened.
//  - abort() before start(): the C++ emits aborted() immediately from abort(); the port
//    records the Aborted state and emits it when StartAsync() runs (deferred by one call —
//    the observable outcome is the same terminal AbortedByUser state).
//  - parseXmlLogs(): the log4j XML event parser IS ported (LogParser) and wired like the C++
//    LaunchTask::onLogLine — but behind the <see cref="XmlParsingEnabled"/> opt-in gate, since
//    the C++ had no off switch and pure-core consumers should keep the simple path. When
//    enabled, StdOut/StdErr lines feed the per-level LogParser instances first: complete
//    log4j events become structured "[HH:mm:ss] [thread/LEVEL] [logger]: message" entries,
//    everything else falls through the same level-detection heuristics as before. The Web
//    host enables it at startup (WebHostBuilder).
//  - censorPrivateInfo() applies the session censor filter; the QMap iteration order (sorted
//    by key) is preserved via the SortedDictionary the instance filter is built into.
using System.Text.RegularExpressions;
using FlintLauncher.Core.Diagnostics;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Tasks;
using FlintLauncher.Core.Text;
using static FlintLauncher.Core.Launch.MessageLevelExtensions;

using System.IO;

namespace FlintLauncher.Core.Launch;

public class LaunchTask : FlintTask
{
    /// <summary>Port of the LaunchTask::State enum.</summary>
    public enum LaunchState
    {
        NotStarted,
        Running,
        Waiting,
        Failed,
        Aborted,
        Finished,
    }

    private static readonly Regex SettingUserRegex = new(".*Setting user.+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Opt-in gate for the port of LaunchTask::parseXmlLogs (the C++ runs it unconditionally):
    /// when true, StdOut/StdErr lines feed the per-level <see cref="LogParser"/> instances
    /// before the level-detection heuristics. The Web host enables it at startup.
    /// </summary>
    public static bool XmlParsingEnabled { get; set; }

    private readonly MinecraftInstance _instance;
    private readonly List<LaunchStep> _steps = [];
    private readonly LogParser _stdoutParser = new();
    private readonly LogParser _stderrParser = new();
    private SortedDictionary<string, string> _censorFilter = new(StringComparer.Ordinal);
    private int _currentStep = -1;
    private LaunchState _state = LaunchState.NotStarted;
    private long _pid = -1;
    private LogModel? _logModel;
    private TaskCompletionSource? _terminalSignal;

    internal LaunchTask(MinecraftInstance instance)
    {
        _instance = instance;
        Init();
    }

    /// <summary>Port of LaunchTask::init(): the instance counts as running from creation.</summary>
    private void Init() => _instance.SetRunning(true);

    /// <summary>Port of the readyForLaunch() signal: the launch preparations are done.</summary>
    public event Action? ReadyForLaunch;

    /// <summary>Port of the requestProgress(Task*) signal.</summary>
    public event Action<FlintTask>? RequestProgress;

    /// <summary>Diagnostics/test hook for the internal LaunchState state machine.</summary>
    internal LaunchState CurrentState => _state;

    /// <summary>
    /// Port of the C++ LaunchTask "Waiting" state observable: the launch preparations are
    /// done and ReadyForLaunch is outstanding — Proceed() will start the game. Used by the
    /// headless host (API) to surface the proceed gate without the internal enum.
    /// </summary>
    public bool IsWaitingForProceed => _state == LaunchState.Waiting;

    public MinecraftInstance Instance => _instance;

    public long Pid
    {
        get => _pid;
        set => _pid = value;
    }

    /// <summary>Port of LaunchTask::isMultiStep(): a launch chain is multi-step by definition.</summary>
    public override bool IsMultiStep => _steps.Count > 1;

    public LogModel GetLogModel()
    {
        if (_logModel is null)
        {
            _logModel = new LogModel();
            _logModel.SetMaxLines(BaseInstance.GetConsoleMaxLines(_instance.Settings()));
            _logModel.SetStopOnOverflow(BaseInstance.ShouldStopOnConsoleOverflow(_instance.Settings()));
            // FIXME: should this really be here?
            _logModel.SetOverflowMessage(
                $"Stopped watching the game log because the log length surpassed {_logModel.GetMaxLines()} lines.\n" +
                "You may have to fix your mods because the game is still logging to files and" +
                " likely wasting harddrive space at an alarming rate!");
        }
        return _logModel;
    }

    public void AppendStep(LaunchStep step) => _steps.Add(step);

    public void PrependStep(LaunchStep step) => _steps.Insert(0, step);

    public void SetCensorFilter(SortedDictionary<string, string> filter) => _censorFilter = filter;

    /// <summary>Port of LaunchTask::create().</summary>
    public static LaunchTask Create(MinecraftInstance instance) => new(instance);

    // ------------------------------------------------------------------- step engine

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        _terminalSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        if (_state == LaunchState.Aborted)
        {
            // C++ abort() before start(): emitAborted() straight away. Deferred to the start
            // call here — see the header note.
            EmitAborted();
            _terminalSignal.TrySetResult();
            return;
        }

        _instance.SetCrashed(false);
        if (_steps.Count == 0)
        {
            _state = LaunchState.Finished;
            EmitSucceeded();
            _terminalSignal.TrySetResult();
            return;
        }
        _state = LaunchState.Running;
        OnStepFinishedCore();
        await _terminalSignal.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Port of onReadyForLaunch(): the current step waits for user confirmation.</summary>
    internal void OnReadyForLaunch()
    {
        _state = LaunchState.Waiting;
        FlintLog.For(FlintLog.Categories.Launch)
            .ReadyForLaunch(StepName(_currentStep));
        ReadyForLaunch?.Invoke();
    }

    /// <summary>Port of onProgressReportingRequested().</summary>
    internal void OnProgressReportingRequested()
    {
        _state = LaunchState.Waiting;
        FlintLog.For(FlintLog.Categories.Launch)
            .ProgressGate(StepName(_currentStep));
        RequestProgress?.Invoke(_steps[_currentStep]);
    }

    /// <summary>Port of onStepFinished(): the initial call starts the first step, later calls
    /// advance (or finalize) the chain.</summary>
    internal void OnStepFinished(FlintTask step) => OnStepFinishedCore();

    /// <summary>Adapter for the failed(QString) signal.</summary>
    internal void OnStepFailed(FlintTask step, string reason) => OnStepFinishedCore();

    private void OnStepFinishedCore()
    {
        if (IsFinished)
        {
            // Late terminal events from a step that was already finalized; the C++ flow never
            // produces them because Qt disconnects finished steps.
            return;
        }

        // initial -> just start the first step
        if (_currentStep == -1)
        {
            _currentStep++;
            StartStep(_steps[_currentStep]);
            return;
        }

        var step = _steps[_currentStep];
        FlintLog.For(FlintLog.Categories.Launch)
            .StepFinished(StepName(_currentStep), step.WasSuccessful, step.FailReason);
        if (step.WasSuccessful)
        {
            // end?
            if (_currentStep == _steps.Count - 1)
            {
                FinalizeSteps(true, "");
            }
            else
            {
                _currentStep++;
                StartStep(_steps[_currentStep]);
            }
        }
        else
        {
            FinalizeSteps(false, step.FailReason);
        }
    }

    private void StartStep(LaunchStep step)
    {
        FlintLog.For(FlintLog.Categories.Launch)
            .StepStarted(_currentStep + 1, _steps.Count, StepName(_currentStep));
        _ = step.StartAsync(); // the step reports its terminal state through the events
    }

    /// <summary>Stable engine-side identity of a step (class name — LaunchStep has no
    /// user-facing name of its own; the user-facing description goes to the launch console
    /// via TextPrint / PrintInstanceInfo).</summary>
    private string StepName(int index) => _steps[index].GetType().Name;

    /// <summary>Port of finalizeSteps(): finalize from the current step backwards, then emit.</summary>
    private void FinalizeSteps(bool successful, string error)
    {
        for (var step = _currentStep; step >= 0; step--)
        {
            _steps[step].FinalizeStep();
        }
        if (successful)
        {
            EmitSucceeded();
        }
        else
        {
            EmitFailed(error);
        }
        _terminalSignal?.TrySetResult();
    }

    // ------------------------------------------------------------- launch confirmation

    /// <summary>Port of proceed(): launch the armed instance (the current step gates on this).
    /// Restores the Running state afterwards so IsWaitingForProceed only reports a genuinely
    /// open gate — see the header deviation note (a no-op Proceed outside Waiting is the
    /// caller's signal to check the state instead of silently retrying).</summary>
    public void Proceed()
    {
        if (_state != LaunchState.Waiting)
        {
            FlintLog.For(FlintLog.Categories.Launch)
                .ProceedIgnored(_state.ToString());
            return;
        }
        FlintLog.For(FlintLog.Categories.Launch)
            .ProceedRouted(StepName(_currentStep));
        _steps[_currentStep].Proceed();
        _state = LaunchState.Running;
    }

    /// <summary>C#-side shorthand for aborting while Waiting (documented addition).</summary>
    public void AbortProceed() => Abort();

    // ------------------------------------------------------------------------- aborting

    /// <summary>Port of canAbort().</summary>
    public bool CanAbort()
    {
        switch (_state)
        {
            case LaunchState.Aborted:
            case LaunchState.Failed:
            case LaunchState.Finished:
                return false;
            case LaunchState.NotStarted:
                return true;
            case LaunchState.Running:
            case LaunchState.Waiting:
                return _steps[_currentStep].CanAbort();
            default:
                return false;
        }
    }

    /// <summary>Port of abort().</summary>
    public override void Abort()
    {
        switch (_state)
        {
            case LaunchState.Aborted:
            case LaunchState.Failed:
            case LaunchState.Finished:
                return;
            case LaunchState.NotStarted:
            {
                _state = LaunchState.Aborted;
                if (IsRunning)
                {
                    EmitAborted();
                    _terminalSignal?.TrySetResult();
                }
                return;
            }
            case LaunchState.Running:
            case LaunchState.Waiting:
            {
                var step = _steps[_currentStep];
                if (!step.CanAbort())
                {
                    return;
                }
                if (step.AbortStep())
                {
                    _state = LaunchState.Aborted;
                }
                return;
            }
            default:
                return;
        }
    }

    // ------------------------------------------------------------------------ logging

    /// <summary>Port of the onLogLines slot.</summary>
    public void OnLogLines(IReadOnlyList<string> lines, MessageLevel defaultLevel = MessageLevel.Launcher)
    {
        foreach (var line in lines)
        {
            OnLogLine(line, defaultLevel);
        }
    }

    /// <summary>Port of the onLogLine slot: the XML parse gate first, then the plain-text path.</summary>
    public void OnLogLine(string line, MessageLevel level = MessageLevel.Launcher)
    {
        if (ParseXmlLogs(line, level))
        {
            return;
        }

        if (level is MessageLevel.StdOut or MessageLevel.StdErr)
        {
            // PlainText branch of the old inlined path: first the "!![LEVEL]!" prefix, then the
            // guessLevel heuristics with the previous entry's level for continuation lines.
            var newLevel = level.TakeFromLine(ref line);
            if (newLevel == MessageLevel.Unknown)
            {
                newLevel = level.GuessLevel(line, GetLogModel().PreviousLevel());
            }
            line = CensorPrivateInfo(line);
            GetLogModel().Append(newLevel, line);
            return;
        }

        // censor private user info
        line = CensorPrivateInfo(line);
        GetLogModel().Append(level, line);
    }

    /// <summary>
    /// Port of LaunchTask::parseXmlLogs: StdOut/StdErr lines feed the per-level LogParser
    /// instances. Complete log4j events become structured "[HH:mm:ss] [thread/LEVEL] [logger]:
    /// message" entries (censor-filtered like every other path); a parse error surfaces as an
    /// Error entry and the line falls back to the plain-text heuristics. Returns true when the
    /// line was consumed by the parser (or buffered as a partial event).
    /// </summary>
    private bool ParseXmlLogs(string line, MessageLevel level)
    {
        if (!XmlParsingEnabled)
        {
            return false;
        }
        LogParser? parser;
        switch (level)
        {
            case MessageLevel.StdErr:
                parser = _stderrParser;
                break;
            case MessageLevel.StdOut:
                parser = _stdoutParser;
                break;
            default:
                return false;
        }

        parser.AppendLine(line);
        var items = parser.ParseAvailable();
        if (parser.GetError() is { } err)
        {
            GetLogModel().Append(MessageLevel.Error,
                $"[Log4j Parse Error] Failed to parse log4j log event: {err.ErrMessage}");
            return false;
        }

        if (items.Count == 0)
        {
            return true; // buffered as a partial event, nothing to show yet
        }

        var model = GetLogModel();
        foreach (var item in items)
        {
            if (item is LogParser.LogEntry entry)
            {
                // the C++ formats the local time as HH:mm:ss; an invalid (missing) timestamp
                // formats as the empty string
                var timestamp = entry.Timestamp is { } ts
                    ? ts.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
                    : "";
                var msg = $"[{timestamp}] [{entry.Thread}/{entry.LevelText}] [{entry.Logger}]: {entry.Message}";
                msg = CensorPrivateInfo(msg);
                model.Append(entry.Level, msg);
            }
            else if (item is LogParser.PlainText text)
            {
                var msg = text.Message;
                var newLevel = level.TakeFromLine(ref msg);
                if (newLevel == MessageLevel.Unknown)
                {
                    newLevel = level.GuessLevel(line, model.PreviousLevel());
                }
                msg = CensorPrivateInfo(msg);
                model.Append(newLevel, msg);
            }
            // (Partial items never leave ParseAvailable.)
        }

        return true;
    }

    /// <summary>Port of emitSucceeded(): the instance is no longer running.</summary>
    protected new void EmitSucceeded()
    {
        _instance.SetRunning(false);
        base.EmitSucceeded();
    }

    /// <summary>Port of emitFailed(QString): the instance counts as crashed.</summary>
    protected new void EmitFailed(string reason)
    {
        _instance.SetRunning(false);
        _instance.SetCrashed(true);
        base.EmitFailed(reason);
    }

    // -------------------------------------------------------------- helpers for steps

    /// <summary>Port of the "Setting user" CloseAfterLaunch stdout filter.</summary>
    public static bool MatchesCloseAfterLaunch(IEnumerable<string> lines) =>
        lines.Any(line => SettingUserRegex.IsMatch(line));

    /// <summary>Port of substituteVariables(): split a command line and expand $VAR/${VAR}
    /// through the instance environment (createLaunchEnvironment when isLaunch).</summary>
    public List<string> SubstituteVariables(string cmd, bool isLaunch = false)
    {
        var env = isLaunch ? _instance.CreateLaunchEnvironment() : _instance.CreateEnvironment();
        return Commandline.Process(cmd, env.Vars);
    }

    /// <summary>Port of censorPrivateInfo().</summary>
    public string CensorPrivateInfo(string input)
    {
        foreach (var (key, value) in _censorFilter)
        {
            input = input.Replace(key, value, StringComparison.Ordinal);
        }
        return input;
    }
}
