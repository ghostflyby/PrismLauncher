// SPDX-License-Identifier: GPL-3.0-only
// In-memory task registry for the async API surface (launch / import / create).
//
// Design:
//  - every long-running core operation is registered as an Entry {id, kind, task}; the
//    HTTP surface returns the id immediately (202) and clients poll /tasks/{id} or stream
//    /tasks/{id}/stream.
//  - launch entries additionally carry the LaunchController; the "waiting" state comes
//    from LaunchTask.IsWaitingForProceed (the ReadyForLaunch gate AutoProceed=false arms).
//  - log lines are pulled from the LaunchTask's LogModel on demand and re-buffered with a
//    monotonically increasing per-entry index, so ?since=N and the SSE stream work even
//    though LogModel itself is a ring buffer that drops old lines. The buffer is capped
//    (oldest lines dropped, indexes preserved).

using FlintLauncher.Core.Diagnostics;
using FlintLauncher.Core.Launch;
using FlintLauncher.Core.Tasks;
using FlintLauncher.Web.Models;

namespace FlintLauncher.Web.Services;

public sealed class FlintTaskRegistry(ILoggerFactory loggerFactory)
{
    private readonly ILogger _log = loggerFactory.CreateLogger(LogCategories.Tasks);

    // the host's factory (DI) — the former FlintLog static facade read the same factory
    // handed over at startup; task lifecycle events land in the same sinks

    public sealed class Entry
    {
        public required string Id { get; init; }
        public required string Kind { get; init; }
        public required FlintTask Task { get; init; }
        public string? InstanceId { get; init; }
        public LaunchController? Controller { get; init; }

        /// <summary>Set when a task completes successfully and the host must re-read state
        /// (e.g. a staged instance commit -> InstanceList.LoadList()).</summary>
        public Action? OnFinished { get; init; }

        /// <summary>Optional terminal payload exposed through the task view (flag: currently
        /// only the mods-update task publishes its found/applied updates here; null = absent,
        /// and the field is omitted from the JSON entirely).</summary>
        public TaskResultView? Result { get; set; }

        // log buffering (see the file header)
        public readonly object LogLock = new();
        public readonly List<LogLineDto> LogBuffer = [];
        public int NextLogIndex;
        public int LastModelCount;
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private int sequence;

    /// <summary>Log lines kept per task (absolute indexes preserved beyond the cap).</summary>
    public const int MaxBufferedLines = 5000;

    public Entry Register(string kind, FlintTask task, string? instanceId = null,
        LaunchController? controller = null, Action? onFinished = null)
    {
        var entry = new Entry
        {
            Id = NewId(),
            Kind = kind,
            Task = task,
            InstanceId = instanceId,
            Controller = controller,
            OnFinished = onFinished,
        };

        void RunHook()
        {
            try
            {
                onFinished?.Invoke();
            }
            catch
            {
                // host bookkeeping must never break the task machinery
            }
        }

        void OnTerminal(FlintTask finishedTask)
        {
            _log.Finished(entry.Id, finishedTask.State.ToString());
            RunHook();
        }

        void OnTerminalFailed(FlintTask finishedTask, string _) => OnTerminal(finishedTask);
        task.Succeeded += OnTerminal;
        task.Failed += OnTerminalFailed;
        task.Aborted += OnTerminal;

        lock (_lock)
        {
            _entries[entry.Id] = entry;
        }

        _log.Registered(entry.Id, kind);
        return entry;
    }

    public Entry? Get(string id)
    {
        lock (_lock)
        {
            return _entries.GetValueOrDefault(id);
        }
    }

    public IReadOnlyList<Entry> All()
    {
        lock (_lock)
        {
            return _entries.Values.ToList();
        }
    }

    private string NewId()
    {
        lock (_lock)
        {
            return $"t{++sequence}-{Guid.NewGuid().ToString("N")[..8]}";
        }
    }

    // ------------------------------------------------------------------ task view

    public TaskView View(Entry entry)
    {
        var state = StateOf(entry);
        var controller = entry.Controller;
        var status = controller?.Launcher?.Status ?? entry.Task.Status;
        return new TaskView(
            entry.Id,
            entry.Kind,
            state,
            status,
            new TaskProgressDto(entry.Task.CurrentProgress, entry.Task.TotalProgress),
            FailReasonOf(entry),
            entry.InstanceId,
            entry.Result);
    }

    public string StateOf(Entry entry)
    {
        // terminal first: a LaunchTask keeps its internal LaunchState at Waiting even after
        // the chain finalized (the FlintTask state carries the terminal outcome)
        if (IsTerminal(entry))
        {
            return MapState(entry.Task.State);
        }

        var launcher = entry.Controller?.Launcher;
        if (launcher is not null && launcher.IsWaitingForProceed)
        {
            return "waiting";
        }

        return "running";
    }

    public bool IsTerminal(Entry entry) =>
        entry.Controller is not null ? entry.Controller.IsFinished : entry.Task.IsFinished;

    // View() FailReason must follow the same rule (only meaningful when terminal)
    public string? FailReasonOf(Entry entry) => IsTerminal(entry) ? entry.Task.FailReason : null;

    private static string MapState(TaskState state) => state switch
    {
        TaskState.Inactive => "pending",
        TaskState.Running => "running",
        TaskState.Succeeded => "succeeded",
        TaskState.Failed => "failed",
        TaskState.AbortedByUser => "aborted",
        _ => "unknown",
    };

    // ------------------------------------------------------------------ abort / proceed

    public bool Abort(Entry entry)
    {
        if (IsTerminal(entry))
        {
            return false;
        }

        // LaunchController.Abort() kills the game (its own ConfirmKill default); plain
        // tasks are cancelled cooperatively.
        (entry.Controller as FlintTask ?? entry.Task).Abort();
        return true;
    }

    /// <summary>Open the ReadyForLaunch gate of a launch task (AutoProceed=false). Returns
    /// false when the entry is not a launch task or the launcher was not built yet.</summary>
    public bool Proceed(Entry entry)
    {
        if (entry.Controller is not { } controller)
        {
            return false;
        }

        var launcher = controller.Launcher;
        if (launcher is null)
        {
            return false;
        }

        launcher.Proceed();
        return true;
    }

    // ------------------------------------------------------------------ log plumbing

    public LogModel? LogModelOf(Entry entry) => entry.Controller?.Launcher?.GetLogModel();

    /// <summary>
    /// Return buffered lines with an index strictly greater than <paramref name="since"/>,
    /// pulling newly appended LogModel entries into the buffer first.
    /// </summary>
    public IReadOnlyList<LogLineDto> ReadLog(Entry entry, int since)
    {
        PullLog(entry);
        lock (entry.LogLock)
        {
            if (since < 0)
            {
                return entry.LogBuffer.ToArray();
            }

            return entry.LogBuffer.Where(l => l.Index > since).ToArray();
        }
    }

    private void PullLog(Entry entry)
    {
        var model = LogModelOf(entry);
        if (model is null)
        {
            return;
        }

        lock (entry.LogLock)
        {
            var entries = model.Entries;
            if (entries.Count > entry.LastModelCount)
            {
                // append everything after the count last seen (ring-buffer drops of older
                // lines only ever shrink the model, which the next branch resyncs)
                for (var i = entry.LastModelCount; i < entries.Count; i++)
                {
                    AppendLocked(entry, entries[i].Level, entries[i].Line);
                }

                entry.LastModelCount = entries.Count;
            }
            else if (entries.Count < entry.LastModelCount)
            {
                // the model shrank (overflow wrap or Clear()): resync, the skipped lines
                // between NextLogIndex and here are lost with the ring buffer
                entry.LastModelCount = entries.Count;
                for (var i = 0; i < entries.Count; i++)
                {
                    AppendLocked(entry, entries[i].Level, entries[i].Line);
                }
            }

            if (entry.LogBuffer.Count > MaxBufferedLines)
            {
                entry.LogBuffer.RemoveRange(0, entry.LogBuffer.Count - MaxBufferedLines);
            }
        }
    }

    private void AppendLocked(Entry entry, MessageLevel level, string line)
    {
        entry.LogBuffer.Add(new LogLineDto(entry.NextLogIndex++, LevelName(level), line));
    }

    private static string LevelName(MessageLevel level) => level switch
    {
        MessageLevel.Unknown => "unknown",
        MessageLevel.StdOut => "stdout",
        MessageLevel.StdErr => "stderr",
        MessageLevel.Launcher => "launcher",
        MessageLevel.Trace => "trace",
        MessageLevel.Debug => "debug",
        MessageLevel.Info => "info",
        MessageLevel.Message => "message",
        MessageLevel.Warning => "warning",
        MessageLevel.Error => "error",
        MessageLevel.Fatal => "fatal",
        _ => "unknown",
    };
}
