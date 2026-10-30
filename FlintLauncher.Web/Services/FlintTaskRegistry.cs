// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
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
        public required TaskHandle Handle { get; init; }
        public string? InstanceId => Handle.InstanceId;
        public LaunchController? Controller => Handle.Controller;

        /// <summary>Optional terminal payload exposed through the task view (flag: currently
        /// only the mods-update task publishes its found/applied updates here; null = absent,
        /// and the field is omitted from the JSON entirely).</summary>
        public TaskResultView? Result
        {
            get => Handle.Result;
            set => Handle.Result = value;
        }

        // log buffering (see the file header)
        public readonly object LogLock = new();
        public readonly List<LogLineDto> LogBuffer = [];
        public int NextLogIndex;
        public int LastModelCount;
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private int _sequence;

    /// <summary>Log lines kept per task (absolute indexes preserved beyond the cap).</summary>
    public const int MaxBufferedLines = 5000;

    /// <summary>Registers a plain-Task operation: the caller supplies the work, the registry
    /// supplies the id and the entry. The work is started with the handle's token, so Abort
    /// reaches it without the caller threading anything. A launch passes its controller so the
    /// API can read the proceed gate and the game log.</summary>
    public Entry Start(string kind, Func<TaskHandle, Task> work, string? instanceId = null,
        Action? onFinished = null, LaunchController? controller = null)
    {
        var handle = new TaskHandle
        {
            Id = NewId(),
            Kind = kind,
            InstanceId = instanceId,
            Controller = controller,
            Stop = controller is null ? null : controller.Abort,
        };

        var entry = Register(handle);
        // the work runs off the request thread: a body with no await before its first heavy
        // stretch (the import's directory scan and copy loop) would otherwise complete inside
        // the POST, making the 202 late and the abort unreachable
        handle.Run = Task.Run(async () =>
        {
            try
            {
                await work(handle).ConfigureAwait(false);
            }
            finally
            {
                RunHook(onFinished);
            }
        });
        return entry;
    }

    /// <summary>Host bookkeeping that must never break the task machinery.</summary>
    private static void RunHook(Action? hook)
    {
        try
        {
            hook?.Invoke();
        }
        catch
        {
            // the operation's outcome is already recorded; a failing hook is the host's problem
        }
    }

    public Entry Register(TaskHandle handle)
    {
        var entry = new Entry
        {
            Id = handle.Id,
            Kind = handle.Kind,
            Handle = handle,
        };

        lock (_lock)
        {
            _entries[entry.Id] = entry;
        }

        _log.Registered(entry.Id, entry.Kind);
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
            return $"t{++_sequence}-{Guid.NewGuid().ToString("N")[..8]}";
        }
    }

    // ------------------------------------------------------------------ task view

    public TaskView View(Entry entry)
    {
        var state = StateOf(entry);
        var status = entry.Handle.Status;
        var (current, total) = entry.Handle.Progress;
        return new TaskView(
            entry.Id,
            entry.Kind,
            state,
            status,
            new TaskProgressDto(current, total),
            FailReasonOf(entry),
            entry.InstanceId,
            entry.Result);
    }

    public string StateOf(Entry entry)
    {
        // the terminal outcome lives on the window; the launch state is only read while the
        // chain is still running
        if (IsTerminal(entry))
        {
            return MapState(entry.Handle.Phase);
        }

        var launcher = entry.Controller?.Launcher;
        if (launcher is not null && launcher.IsWaitingForProceed)
        {
            return "waiting";
        }

        return entry.Handle.Phase == TaskPhase.Pending ? "pending" : "running";
    }

    public bool IsTerminal(Entry entry) => entry.Handle.IsTerminal;

    // View() FailReason must follow the same rule (only meaningful when terminal)
    public string? FailReasonOf(Entry entry) => IsTerminal(entry) ? entry.Handle.FailReason : null;

    private static string MapState(TaskPhase phase) => phase switch
    {
        TaskPhase.Pending => "pending",
        TaskPhase.Running => "running",
        TaskPhase.Waiting => "waiting",
        TaskPhase.Succeeded => "succeeded",
        TaskPhase.Failed => "failed",
        TaskPhase.Aborted => "aborted",
        _ => "unknown",
    };

    // ------------------------------------------------------------------ abort / proceed

    public bool Abort(Entry entry)
    {
        if (IsTerminal(entry))
        {
            return false;
        }

        // the launch controller kills the game (its own ConfirmKill default); plain work is
        // cancelled cooperatively through the handle's token
        entry.Handle.Abort();
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
