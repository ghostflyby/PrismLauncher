// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022 Sefa Eyeoglu <contact@scrumplex.net>
//
// This file incorporates work covered by the following copyright and
// permission notice:
//
//     Copyright 2013-2021 MultiMC Contributors
//
//     Licensed under the Apache License, Version 2.0 (the "License");
//     you may not use this file except in compliance with the License.
//     You may obtain a copy of the License at
//
//         http://www.apache.org/licenses/LICENSE-2.0
//
//     Unless required by applicable law or agreed to in writing, software
//     distributed under the License is distributed on an "AS IS" BASIS,
//     WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//     See the License for the specific language governing permissions and
//     limitations under the License.
//
// The step engine of one instance launch. Steps are declared in stages: a stage's steps start
// together and the next stage waits for all of them, so independent work (the library, asset
// and java downloads) shares a stage while anything reading its output gets a later one.
//
// The engine awaits each step's body. The launch's outcome is the run's: a return succeeds, a
// thrown exception fails, and a cancellation aborts. `await RunAsync()` therefore covers the
// whole session, including the time the game runs — a step whose end is the game exiting parks
// its body on that event.
//
// The "run the game now" gate: a step emits ReadyForLaunch and waits; the host answers with
// Proceed(), which routes to that step. Proceed() restores Running so a host reading the state
// does not see "waiting" through the whole game session.
//
// abort() before the run aborts it without starting anything. An abort while a step runs asks
// that step (AbortStep) and cancels the chain's token; the stages after it never start.
//
// parseXmlLogs(): the log4j XML event parser (LogParser) runs behind the
// <see cref="LaunchOptions.XmlLogParsing"/> opt-in gate, since pure-core consumers should keep
// the simple path. When enabled, StdOut/StdErr lines feed the per-level LogParser instances
// first: complete log4j events become structured "[HH:mm:ss] [thread/LEVEL] [logger]: message"
// entries, and everything else falls through the plain-text level heuristics.
//
// censorPrivateInfo() applies the session censor filter, iterated in key order.

using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using FlintLauncher.Core.Diagnostics;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Tasks;
using FlintLauncher.Core.Text;
using Microsoft.Extensions.Logging;

namespace FlintLauncher.Core.Launch;

public sealed class LaunchTask
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

    /// <summary>How a launch ended, as the engine settled it: a returned run succeeds, a
    /// failure carries its reason, and an abort is its own outcome.</summary>
    public readonly record struct LaunchOutcome(bool Succeeded, string FailReason, bool Aborted);

    private static readonly Regex SettingUserRegex = new(".*Setting user.+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly MinecraftInstance _instance;
    private readonly ILoggerFactory _loggerFactory;
    private readonly bool _xmlLogParsing;
    private readonly List<List<LaunchStep>> _stages = [];
    private readonly LogParser _stdoutParser = new();
    private readonly LogParser _stderrParser = new();
    private SortedDictionary<string, string> _censorFilter = new(StringComparer.Ordinal);
    private int _stageIndex = -1;
    private readonly List<LaunchStep> _gatedSteps = [];
    private readonly List<LaunchStep> _runningSteps = [];
    private readonly Lock _gateLock = new();
    private LaunchStep? _gatedStep;
    private volatile bool _abortRequested;
    private CancellationTokenSource? _abortSource;
    private LaunchState _state = LaunchState.NotStarted;
    private LogModel? _logModel;

    /// <summary>The engine for one launch; the composition point is
    /// <see cref="LaunchFactory"/>. A bare task (no steps yet) is a valid test/host shape.</summary>
    public LaunchTask(MinecraftInstance instance, ILoggerFactory loggerFactory,
        bool xmlLogParsing = false)
    {
        _instance = instance;
        _loggerFactory = loggerFactory;
        _xmlLogParsing = xmlLogParsing;
        Init();
    }

    /// <summary>Port of LaunchTask::init(): the instance counts as running from creation.</summary>
    private void Init() => _instance.SetRunning(true);

    /// <summary>Port of the readyForLaunch() signal: the launch preparations are done.</summary>
    public event Action? ReadyForLaunch;

    /// <summary>Raised when a step asks the host to open the progress gate for it.</summary>
    public event Action<LaunchStep>? RequestProgress;

    /// <summary>Diagnostics/test hook for the internal LaunchState state machine.</summary>
    internal LaunchState CurrentState => _state;

    /// <summary>Runs the chain and reports its outcome as the state the engine settled on,
    /// for callers that inspect the launch rather than propagate its failure.</summary>
    public async Task<LaunchOutcome> RunToCompletionAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RunAsync(cancellationToken).ConfigureAwait(false);
            return new LaunchOutcome(true, "", false);
        }
        catch (OperationCanceledException)
        {
            return new LaunchOutcome(false, "Aborted", true);
        }
        catch (Exception e)
        {
            return new LaunchOutcome(false, TaskWork.ReasonOf(e), false);
        }
    }

    /// <summary>
    /// Port of the C++ LaunchTask "Waiting" state observable: the launch preparations are
    /// done and ReadyForLaunch is outstanding — Proceed() will start the game. Used by the
    /// headless host (API) to surface the proceed gate without the internal enum.
    /// </summary>
    public bool IsWaitingForProceed => _state == LaunchState.Waiting;

    public MinecraftInstance Instance => _instance;

    /// <summary>Diagnostics logger (flint.launch category). Resolved per call from the
    /// injected factory — a disposed host's factory degrades to silence, never throws.</summary>
    private ILogger Log => Diagnostics.Log.For(_loggerFactory, LogCategories.Launch);

    public long Pid { get; set; } = -1;

    /// <summary>Whether the launch runs more than a single step.</summary>
    public bool IsMultiStep => _stages.Count > 1 || (_stages.Count == 1 && _stages[0].Count > 1);

    /// <summary>Port of onReadyForLaunch(): the step that asked waits for user confirmation.
    /// Several gated steps may be open at once; <see cref="Proceed"/> consumes them in the
    /// order they asked.</summary>
    internal void OnReadyForLaunch(LaunchStep step)
    {
        lock (_gateLock)
        {
            _gatedSteps.Add(step);
            _gatedStep ??= step;
        }

        _state = LaunchState.Waiting;
        Log.ReadyForLaunch(StepName(step));
        ReadyForLaunch?.Invoke();
    }

    /// <summary>Port of onProgressReportingRequested(): the step that asked waits for the
    /// host to open the gate.</summary>
    internal void OnProgressReportingRequested(LaunchStep step)
    {
        lock (_gateLock)
        {
            _gatedSteps.Add(step);
            _gatedStep ??= step;
        }

        _state = LaunchState.Waiting;
        Log.ProgressGate(StepName(step));
        RequestProgress?.Invoke(step);
    }

    /// <summary>Stable engine-side identity of a step (class name — LaunchStep has no
    /// user-facing name of its own; the user-facing description goes to the launch console
    /// via TextPrint / PrintInstanceInfo).</summary>
    private static string StepName(LaunchStep step) => step.GetType().Name;

    public LogModel GetLogModel()
    {
        if (_logModel is null)
        {
            _logModel = new LogModel
            {
                MaxLines = BaseInstance.GetConsoleMaxLines(_instance.Settings),
                StopOnOverflow = BaseInstance.ShouldStopOnConsoleOverflow(_instance.Settings)
            };
            // FIXME: should this really be here?
            _logModel.OverflowMessage = $"Stopped watching the game log because the log length surpassed {_logModel.MaxLines} lines.\n" +
                                        "You may have to fix your mods because the game is still logging to files and" +
                                        " likely wasting harddrive space at an alarming rate!";
        }

        return _logModel;
    }

    /// <summary>Appends one step as its own stage: it runs after everything appended so far.</summary>
    public void AppendStep(LaunchStep step) => _stages.Add([step]);

    /// <summary>Appends a stage whose steps run concurrently and whose aggregate gates the
    /// stages after it. The steps must not depend on each other's output.</summary>
    public void AppendStage(params LaunchStep[] steps) => _stages.Add([.. steps]);

    /// <summary>Inserts one step as a stage before everything else (the controller's banner
    /// steps); prepending several keeps the order of the prepend calls.</summary>
    public void PrependStep(LaunchStep step) => _stages.Insert(0, [step]);

    public void SetCensorFilter(SortedDictionary<string, string> filter) => _censorFilter = filter;

    // ------------------------------------------------------------------- step engine

    /// <summary>Runs the stages. The chain's outcome is the terminal state of this task: a
    /// return succeeds it, a thrown exception fails it, and an abort requested through
    /// <see cref="Abort"/> aborts it.</summary>
    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        if (_state == LaunchState.Aborted)
        {
            // abort() before start(): nothing runs, and the run reports the abort
            _instance.SetRunning(false);
            throw new OperationCanceledException("Aborted");
        }

        _instance.SetCrashed(false);
        if (_stages.Count == 0)
        {
            _state = LaunchState.Finished;
            _instance.SetRunning(false);
            return;
        }

        _state = LaunchState.Running;
        using var abortSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _abortSource = abortSource;
        var token = abortSource.Token;

        Exception? failure = null;
        try
        {
            for (_stageIndex = 0; _stageIndex < _stages.Count; _stageIndex++)
            {
                if (_abortRequested)
                {
                    // an abort was honoured while a later stage had not started: do not start it
                    break;
                }

                await RunStageAsync(_stages[_stageIndex], token).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
        {
            failure = e;
        }
        finally
        {
            _abortSource = null;
            // the C++ finalizes every started step backwards once the chain ends, whether it
            // succeeded or not
            FinalizeStartedSteps();
            // the instance stops counting as running with the chain, on every exit path
            // (including a token-cancelled abort, which leaves through the exception)
            _instance.SetRunning(false);
        }

        // A step's own failure wins over the abort flag: an aborted step usually reports how it
        // ended (the game crashed on the abort command), and that reason is what the caller
        // asked for. The flag only stops the stages after the abort.
        _state = failure switch
        {
            null when _abortRequested => LaunchState.Aborted,
            null => LaunchState.Finished,
            _ => LaunchState.Failed,
        };

        if (failure is not null)
        {
            _instance.SetCrashed(true);
            ExceptionDispatchInfo.Capture(failure).Throw();
            return;
        }

        if (_abortRequested || cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Aborted", cancellationToken);
        }
    }

    /// <summary>Finalizes the steps that ran, from the deepest started stage back to the first.
    /// A finalizer that throws must not stop the ones before it from running.</summary>
    private void FinalizeStartedSteps()
    {
        // _stageIndex is past the last stage when the chain completed, and names the failed
        // one when it did not
        var lastStarted = Math.Min(_stageIndex, _stages.Count - 1);
        for (var stage = lastStarted; stage >= 0; stage--)
        {
            var steps = _stages[stage];
            for (var i = steps.Count - 1; i >= 0; i--)
            {
                try
                {
                    steps[i].FinalizeStep();
                }
                catch (Exception)
                {
                    // a cleanup failure must not mask the launch outcome or skip the remaining
                    // cleanups
                }
            }
        }
    }

    /// <summary>Runs one stage: its steps start together, and the stage waits for all of them
    /// before the next one begins. A failure is collected rather than thrown so its siblings
    /// still run to their own end.</summary>
    private async Task RunStageAsync(IReadOnlyList<LaunchStep> stage, CancellationToken cancellationToken)
    {
        var failures = new List<Exception>();
        if (stage.Count == 1)
        {
            await RunStepAsync(stage[0], cancellationToken, failures).ConfigureAwait(false);
        }
        else
        {
            await Task.WhenAll(stage.Select(step =>
                RunStepAsync(step, cancellationToken, failures))).ConfigureAwait(false);
        }

        if (failures.Count > 0)
        {
            // one failure keeps its own shape (the caller sees its message); several are
            // aggregated, since a stage reports them together
            throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
        }
    }

    /// <summary>Runs one step to its end. A step that fails contributes its exception to the
    /// stage's failure list instead of throwing into the caller, so its siblings still run.</summary>
    private async Task RunStepAsync(LaunchStep step, CancellationToken cancellationToken,
        List<Exception> failures)
    {
        lock (_gateLock)
        {
            _runningSteps.Add(step);
        }

        Log.StepStarted(_stageIndex + 1, _stages.Count, StepName(step));
        try
        {
            await step.RunAsync(cancellationToken).ConfigureAwait(false);
            Log.StepFinished(StepName(step), !_abortRequested, _abortRequested ? "Aborted" : "");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // the launch is being aborted: the caller surfaces the cancellation
            throw;
        }
        catch (Exception e)
        {
            Log.StepFinished(StepName(step), false, e.Message);
            lock (failures)
            {
                failures.Add(e);
            }
        }
        finally
        {
            lock (_gateLock)
            {
                _runningSteps.Remove(step);
                _gatedSteps.Remove(step);
                if (ReferenceEquals(_gatedStep, step))
                {
                    _gatedStep = _gatedSteps.Count > 0 ? _gatedSteps[0] : null;
                }
            }
        }
    }

    // ------------------------------------------------------------- launch confirmation

    /// <summary>Port of proceed(): open the gate the waiting step asked for. A no-op outside
    /// the Waiting state is the caller's signal to check the state instead of retrying.</summary>
    public void Proceed()
    {
        LaunchStep? step;
        lock (_gateLock)
        {
            step = _gatedStep;
            if (step is not null)
            {
                _gatedSteps.Remove(step);
                _gatedStep = _gatedSteps.Count > 0 ? _gatedSteps[0] : null;
            }
        }

        if (_state != LaunchState.Waiting || step is null)
        {
            Log.ProceedIgnored(_state.ToString());
            return;
        }

        Log.ProceedRouted(StepName(step));
        step.Proceed();
        if (_gatedStep is null)
        {
            _state = LaunchState.Running;
        }
    }

    // ------------------------------------------------------------------------- aborting

    /// <summary>Port of canAbort(). Between stages no step is running, but the chain still can
    /// be stopped, so the answer is true while it has not finished.</summary>
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
            {
                LaunchStep[] candidates;
                lock (_gateLock)
                {
                    candidates = _gatedSteps.Count > 0 ? [.. _gatedSteps] : [.. _runningSteps];
                }

                // no step in flight: the stage loop is between stages and can still stop
                return candidates.Length == 0 || candidates.Any(step => step.CanAbort());
            }
            default:
                return false;
        }
    }

    /// <summary>Stops the launch. A running step decides how: AbortStep() stops whatever it
    /// drives (the game process, the pre-launch command), and the chain's token stops the
    /// stages that have not started.</summary>
    public void Abort()
    {
        switch (_state)
        {
            case LaunchState.Aborted:
            case LaunchState.Failed:
            case LaunchState.Finished:
                return;
            case LaunchState.NotStarted:
                _state = LaunchState.Aborted;
                _abortRequested = true;
                return;
            case LaunchState.Running:
            case LaunchState.Waiting:
            {
                LaunchStep[] candidates;
                lock (_gateLock)
                {
                    candidates = _gatedSteps.Count > 0 ? [.. _gatedSteps] : [.. _runningSteps];
                }

                var honoured = false;
                foreach (var step in candidates)
                {
                    if (step.CanAbort() && step.AbortStep())
                    {
                        honoured = true;
                    }
                }

                // between stages nothing is left to abort, but the chain still stops rather
                // than starting the next stage
                if (honoured || candidates.Length == 0)
                {
                    _abortRequested = true;
                    _state = LaunchState.Aborted;
                    // a body parked on the token ends here. A step that drives something the
                    // token cannot reach (the game process) stopped it in AbortStep() and
                    // reports how it ended.
                    _abortSource?.Cancel();
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
        if (!_xmlLogParsing)
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
                    ? ts.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)
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

    // -------------------------------------------------------------- helpers for steps

    /// <summary>The "Setting user" CloseAfterLaunch stdout filter.</summary>
    public static bool MatchesCloseAfterLaunch(IEnumerable<string> lines) =>
        lines.Any(line => SettingUserRegex.IsMatch(line));

    /// <summary>Port of substituteVariables(): split a command line and expand $VAR/${VAR}
    /// through the instance environment (createLaunchEnvironment when isLaunch).</summary>
    public List<string> SubstituteVariables(string cmd, bool isLaunch = false)
    {
        var env = isLaunch ? _instance.CreateLaunchEnvironment() : _instance.CreateEnvironment();
        return Commandline.Process(cmd, env);
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
