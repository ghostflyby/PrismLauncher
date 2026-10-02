// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/tasks/ConcurrentTask.{h,cpp}.
//
// Runs a list of tasks concurrently (up to `maxConcurrent`, C++ constructor default: 6).
// Failure semantics ported from the C++: a failing subtask neither aborts its siblings nor
// fails the parent right away — every subtask runs to completion, and only once the queue and
// the running set are drained does the parent finish (success, the single subtask's failure
// reason, or a combined "Multiple subtasks failed" message). A subtask that aborts counts as a
// failure with the reason "Aborted" (C++ wires Task::aborted to subTaskFailed(next, "Aborted")).
// Qt queued connections become Task.Yield() plus a lock around the queue/step bookkeeping;
// Abort() mirrors the C++ parent-side behavior of finishing immediately instead of waiting for
// the subtasks (with `_aborting` playing the role of the disconnect(child, aborted, this)).
namespace FlintLauncher.Core.Tasks;

/// <summary>
/// Runs a list of tasks concurrently (according to the `maxConcurrent` parameter). Behaviour is
/// the same as a regular <see cref="FlintTask"/> (e.g. starts using StartAsync()).
/// </summary>
public class ConcurrentTask : FlintTask
{
    /// <summary>C++ constructor default: explicit ConcurrentTask(QString task_name = "", int max_concurrent = 6).</summary>
    public const int DefaultMaxConcurrent = 6;

    private protected readonly object _gate = new();
    private protected readonly Queue<FlintTask> _queue = new();
    private protected readonly Dictionary<FlintTask, Task?> _doing = new();
    private protected readonly Dictionary<FlintTask, FlintTask> _done = new();
    private protected readonly Dictionary<FlintTask, FlintTask> _failed = new();
    private protected readonly Dictionary<FlintTask, FlintTask> _succeeded = new();
    private protected readonly Dictionary<Guid, TaskStepProgress> _taskProgress = new();
    private protected int _totalMaxSize;
    private CancellationToken _runToken;
    private volatile bool _aborting;
    /// <summary>
    /// Subtask completions whose bookkeeping is still in flight (between RunSubTaskAsync's yield
    /// and OnSubTaskFinished returning). The driver loop must not start new work while this is
    /// non-zero, otherwise it can observe a drained _doing before the completion handler decided
    /// the outcome and start a subtask that should never have run (SequentialTask fail-fast).
    /// </summary>
    private int _pendingCompletions;
    /// <summary>
    /// Gate-guarded stamp bumped on every composition mutation (subtask finished / next
    /// dequeued). UpdateState captures the stamp together with its snapshot; the emission is
    /// serialized through <see cref="_emitLock"/> and DROPPED when superseded — concurrent
    /// completions finish their bookkeeping in any order, and without this a slower thread's
    /// STALE snapshot could be the last status emitted (the C++ got ordering for free from
    /// the single-threaded event loop; ConcurrentTaskAggregatesProgressTotals caught it).
    /// </summary>
    private long _stateStamp;
    private readonly object _emitLock = new();

    public ConcurrentTask(string taskName = "", int maxConcurrent = DefaultMaxConcurrent)
    {
        // `taskName` mirrors the C++ QObject objectName, which has no equivalent here.
        _totalMaxSize = maxConcurrent;
    }

    /// <summary>Adds a task to execute in this ConcurrentTask (safe to call before starting).</summary>
    public void AddTask(FlintTask task)
    {
        lock (_gate)
        {
            _queue.Enqueue(task);
        }
    }

    /// <summary>
    /// Port of ConcurrentTask::clear(): resets the internal state of the task. This allows the
    /// same task to be re-used. (Like the C++ original, m_succeeded is left alone.)
    /// </summary>
    public void Clear()
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("Cannot clear a ConcurrentTask while it is running.");
        }

        lock (_gate)
        {
            _done.Clear();
            _failed.Clear();
            _queue.Clear();
            _taskProgress.Clear();
        }
        ResetProgress();
    }

    public override bool IsMultiStep => TotalSize() > 1;

    public override void Abort()
    {
        // Port of ConcurrentTask::abort(): drop everything that has not started yet, then abort
        // the running subtasks. Like the C++ original, the parent finishes immediately instead of
        // waiting for the subtasks; `_aborting` mirrors disconnect(child, aborted, this) so that
        // late subtask completions are ignored.
        _aborting = true;

        lock (_gate)
        {
            _queue.Clear();
        }

        FlintTask[] doing;
        lock (_gate)
        {
            doing = _doing.Keys.ToArray();
        }
        foreach (var task in doing)
        {
            task.Abort();
        }

        // Wake the driver loop in ExecuteAsync even if subtasks ignore cancellation.
        base.Abort();

        // C++ emitAborted(): guarded by isRunning(), so this is a no-op when not running
        // (the C++ bypass of emitting aborted()/finished() without a state change is not ported).
        EmitAborted();
    }

    public override IReadOnlyList<TaskStepProgress> GetStepProgress()
    {
        // Port of ConcurrentTask::getStepProgress(): the progress entries of the started,
        // not-yet-finished subtasks.
        lock (_gate)
        {
            return [.. _taskProgress.Values];
        }
    }

    /// <summary>
    /// Aggregation port of the C++ totalProgress()/stepProgress() semantics: the overall progress
    /// of a composite is "done subtasks out of the total size" (ConcurrentTask::updateState).
    /// </summary>
    public override TaskStepProgress GetProgress()
    {
        lock (_gate)
        {
            return new TaskStepProgress(Uid)
            {
                Current = _done.Count,
                Total = TotalSizeLocked(),
                Status = Status,
                Details = GetDetails(),
                State = ToStepState(State),
            };
        }
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        _runToken = cancellationToken;
        _aborting = false; // allow reuse after Abort(), like any Task restart

        // Port of ConcurrentTask::executeTask(): queue one start per concurrency slot.
        // Extra invocations no-op while the running set is full or the queue is empty.
        for (var i = 0; i < _totalMaxSize; i++)
        {
            ExecuteNextSubTask();
        }

        // The C++ task is purely event driven after executeTask(); this driver loop keeps
        // StartAsync pending until the task reaches a terminal state. ExecuteNextSubTask is the
        // single arbiter: it starts queued work, no-ops while slots are busy, and finishes the
        // task once queue and doing are both empty. Sampling between a subtask's removal from
        // _doing and the next start must not be mistaken for "done", hence the yield-and-recheck.
        // A null entry is a committed reservation (a subtask will run, or its completion
        // bookkeeping is mid-flight on another thread) — waiting is the only correct move while
        // one is visible; the driver must never run its own ExecuteNextSubTask on top of it.
        var cancelled = new TaskCompletionSource();
        using (cancellationToken.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), cancelled))
        {
            while (State == TaskState.Running)
            {
                Task? pending = null;
                var reserved = false;
                lock (_gate)
                {
                    foreach (var runTask in _doing.Values)
                    {
                        if (runTask is null)
                        {
                            reserved = true;
                            continue;
                        }
                        if (!runTask.IsCompleted)
                        {
                            pending = runTask;
                            break;
                        }
                    }
                }

                if (pending is null)
                {
                    // Wait for in-flight completion bookkeeping (or a reservation being filled)
                    // before (re)starting anything, so the completion handler's decision (e.g.
                    // SequentialTask failing the parent) is observed atomically by this loop.
                    if (reserved || Volatile.Read(ref _pendingCompletions) > 0)
                    {
                        await Task.Yield();
                        continue;
                    }
                    // Either everything drained (FinishLocked decides) or a subtask is between
                    // its reservation and its run task being stored.
                    ExecuteNextSubTask();
                    if (State != TaskState.Running)
                    {
                        break;
                    }
                    await Task.Yield();
                    continue;
                }

                await Task.WhenAny(pending, cancelled.Task).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    /// <summary>Port of ConcurrentTask::executeNextSubTask().</summary>
    protected virtual void ExecuteNextSubTask()
    {
        if (State != TaskState.Running)
        {
            return;
        }

        FlintTask? next = null;
        lock (_gate)
        {
            if (_doing.Count >= _totalMaxSize)
            {
                return;
            }

            if (_queue.Count > 0)
            {
                next = _queue.Dequeue();
                // Reserve the bookkeeping slot now; the run task is filled in by StartSubTask
                // (C++ inserts into m_doing before the queued child start).
                _doing[next] = null;
                _taskProgress[next.Uid] = new TaskStepProgress(next.Uid);
                _stateStamp++;
            }
            else if (_doing.Count == 0)
            {
                // The queue and the running set are empty: the task is done.
                FinishLocked();
                return;
            }
            // else: nothing to do while subtasks are still running.
        }

        if (next is not null)
        {
            StartSubTask(next);
        }
    }

    /// <summary>Port of ConcurrentTask::startSubTask() (minus the bookkeeping done by the caller).</summary>
    private void StartSubTask(FlintTask next)
    {
        next.StatusChanged += OnSubTaskStatus;
        next.Details += OnSubTaskDetails;
        next.Progress += OnSubTaskProgress;

        UpdateState();

        var runTask = RunSubTaskAsync(next);
        lock (_gate)
        {
            // If the subtask already finished, its completion handler removed the entry; don't
            // resurrect it.
            if (_doing.TryGetValue(next, out var reserved) && reserved is null)
            {
                _doing[next] = runTask;
            }
        }
    }

    private async Task RunSubTaskAsync(FlintTask next)
    {
        try
        {
            await next.StartAsync(_runToken).ConfigureAwait(false);
        }
        catch
        {
            // StartAsync never throws; keep the port defensive anyway.
        }

        // Mirror the C++ Qt::QueuedConnection between a subtask finishing and the parent's
        // bookkeeping; also keeps long chains of synchronous subtasks from recursing deeply.
        // The completion window is announced BEFORE the yield parks this continuation: from
        // this moment the subtask is (or is about to be) terminal, so every observer must see
        // _pendingCompletions > 0 until the outcome (success/failure/parent-fail) has been
        // fully applied — no window where a terminal subtask is unannounced.
        Interlocked.Increment(ref _pendingCompletions);
        try
        {
            await Task.Yield();
            OnSubTaskFinished(next);
        }
        finally
        {
            Interlocked.Decrement(ref _pendingCompletions);
        }
    }

    private void OnSubTaskFinished(FlintTask next)
    {
        if (_aborting)
        {
            // ConcurrentTask::abort() disconnects the subtask's signals; ignore late completions.
            return;
        }

        if (next.State == TaskState.Succeeded)
        {
            SubTaskSucceeded(next);
        }
        else
        {
            // C++ wires Task::aborted to subTaskFailed(next, "Aborted") as well.
            SubTaskFailed(next, next.State == TaskState.AbortedByUser ? "Aborted" : next.FailReason);
        }
    }

    /// <summary>Port of ConcurrentTask::subTaskSucceeded().</summary>
    private void SubTaskSucceeded(FlintTask task)
    {
        SubTaskFinished(task, TaskStepState.Succeeded);
        ExecuteNextSubTask(); // C++ queues executeNextSubTask after a subtask finishes
    }

    /// <summary>Port of ConcurrentTask::subTaskFailed(); SequentialTask overrides this.</summary>
    protected virtual void SubTaskFailed(FlintTask task, string msg)
    {
        SubTaskFinished(task, TaskStepState.Failed);
        ExecuteNextSubTask();
    }

    /// <summary>Port of ConcurrentTask::subTaskFinished().</summary>
    private protected void SubTaskFinished(FlintTask task, TaskStepState state)
    {
        TaskStepProgress finalProgress = default;
        var emitProgress = false;
        lock (_gate)
        {
            _done[task] = task;
            if (state == TaskStepState.Succeeded)
            {
                _succeeded[task] = task;
            }
            else
            {
                _failed[task] = task;
            }
            _doing.Remove(task);
            _stateStamp++;

            // C++ copies the progress entry by value, sets the state and removes it from the map.
            if (_taskProgress.Remove(task.Uid, out var progress))
            {
                progress.State = state;
                finalProgress = progress;
                emitProgress = true;
            }
        }

        // C++ disconnects the subtask from all parent signals here.
        task.StatusChanged -= OnSubTaskStatus;
        task.Details -= OnSubTaskDetails;
        task.Progress -= OnSubTaskProgress;

        if (emitProgress)
        {
            EmitProgress(finalProgress);
        }
        UpdateState();
    }

    /// <summary>Port of ConcurrentTask::subTaskStatus().</summary>
    private void OnSubTaskStatus(FlintTask task, string msg)
    {
        TaskStepProgress progress;
        int totalSize;
        lock (_gate)
        {
            if (!_taskProgress.TryGetValue(task.Uid, out progress))
            {
                return;
            }
            progress.Status = msg;
            progress.State = TaskStepState.Running;
            _taskProgress[task.Uid] = progress;
            totalSize = TotalSizeLocked();
        }

        EmitProgress(progress);
        if (totalSize == 1)
        {
            EmitStatus(msg);
        }
    }

    /// <summary>Port of ConcurrentTask::subTaskDetails().</summary>
    private void OnSubTaskDetails(FlintTask task, string msg)
    {
        TaskStepProgress progress;
        int totalSize;
        lock (_gate)
        {
            if (!_taskProgress.TryGetValue(task.Uid, out progress))
            {
                return;
            }
            progress.Details = msg;
            progress.State = TaskStepState.Running;
            _taskProgress[task.Uid] = progress;
            totalSize = TotalSizeLocked();
        }

        EmitProgress(progress);
        if (totalSize == 1)
        {
            EmitDetails(msg);
        }
    }

    /// <summary>Port of ConcurrentTask::subTaskProgress().</summary>
    private void OnSubTaskProgress(FlintTask task, TaskStepProgress taskProgress)
    {
        TaskStepProgress progress;
        int totalSize;
        lock (_gate)
        {
            if (!_taskProgress.TryGetValue(task.Uid, out progress))
            {
                return;
            }
            progress.Update(taskProgress.Current, taskProgress.Total);
            _taskProgress[task.Uid] = progress;
            totalSize = TotalSizeLocked();
        }

        EmitProgress(progress);
        UpdateState();

        if (totalSize == 1)
        {
            SetProgress(progress.Current, progress.Total);
        }
    }

    /// <summary>The terminal decision once the queue and the running set are empty (gate held).</summary>
    private void FinishLocked()
    {
        if (_failed.Count == 0)
        {
            EmitSucceeded();
            return;
        }

        if (_failed.Count == 1)
        {
            var task = _failed.Keys.First();
            var reason = task.FailReason;
            if (reason.Length == 0)
            {
                reason = "Task failed"; // "clearly a bug somewhere"
            }
            EmitFailed(reason);
            return;
        }

        List<string> failReasons = [];
        foreach (var failed in _failed.Values)
        {
            var reason = failed.FailReason;
            if (reason.Length > 0)
            {
                failReasons.Add(reason);
            }
        }
        EmitFailed(failReasons.Count == 0
            ? "Multiple subtasks failed"
            : $"Multiple subtasks failed\n{string.Join("\n", failReasons)}");
    }

    /// <summary>Port of ConcurrentTask::updateState(). Snapshot and stamp are captured
    /// atomically under the gate; the emission is stamped so a superseded (older-snapshot)
    /// update can never be the last one emitted.</summary>
    protected virtual void UpdateState()
    {
        long stamp;
        int doing, done, queued, totalSize;
        lock (_gate)
        {
            stamp = ++_stateStamp;
            doing = _doing.Count;
            done = _done.Count;
            queued = _queue.Count;
            totalSize = TotalSizeLocked();
        }

        if (totalSize > 1)
        {
            EmitStateUpdate(stamp, () =>
            {
                SetProgress(done, totalSize);
                EmitStatus($"Executing {doing} task(s) ({done} out of {totalSize} are done)");
            });
        }
        else
        {
            var status = "Please wait...";
            if (queued > 0)
            {
                status = "Waiting for a task to start...";
            }
            else if (doing > 0)
            {
                status = "Executing 1 task:";
            }
            else if (done > 0)
            {
                status = "Task finished.";
            }
            EmitStateUpdate(stamp, () => EmitStatus(status));
        }
    }

    /// <summary>Capture a state stamp (gate held): the caller's snapshot is the freshest for
    /// this stamp until a later gate section bumps it again.</summary>
    private protected long BeginStateUpdateLocked() => ++_stateStamp;

    /// <summary>Emit one stamped state update. A superseded stamp (a later mutation already
    /// captured a newer snapshot) is dropped: concurrent completions may reach this point in
    /// any order, but the LAST emitted status always reflects the LATEST snapshot — and at
    /// quiescence the latest snapshot's emitter is exactly the one whose check still holds.</summary>
    private protected void EmitStateUpdate(long stamp, Action emit)
    {
        lock (_emitLock)
        {
            if (Volatile.Read(ref _stateStamp) != stamp)
            {
                return; // a newer snapshot exists — its own update supersedes this one
            }
            emit();
        }
    }

    /// <summary>Port of ConcurrentTask::totalSize(): queue + doing + done (lock free variant).</summary>
    private int TotalSize()
    {
        lock (_gate)
        {
            return TotalSizeLocked();
        }
    }

    /// <summary>totalSize() for callers that already hold <see cref="_gate"/>.</summary>
    private protected int TotalSizeLocked() => _queue.Count + _doing.Count + _done.Count;
}
