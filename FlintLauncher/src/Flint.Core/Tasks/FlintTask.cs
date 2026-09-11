// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/tasks/Task.{h,cpp}.
//
// The class is renamed Task -> FlintTask to avoid the clash with System.Threading.Tasks.Task.
// The Qt signal/slot machinery becomes C# events, Task::start() becomes the awaited
// StartAsync() and abort() maps to CancellationTokenSource.Cancel() which ExecuteAsync
// implementations observe (OperationCanceledException routes to the AbortedByUser state, any
// other exception routes to EmitFailed, so StartAsync never throws).
// Not ported (Qt/UI specific or absent from the pinned API): the QRunnable plumbing, the
// started()/finished()/warningLogged()/abortButtonTextChanged()/abortStatusChanged() signals,
// propagateFromOther(), warnings()/logWarning() and the m_can_abort gate (Abort() always
// requests cancellation). Additive ports of public C++ API kept under their C++ names:
// WasSuccessful (wasSuccessful), IsMultiStep (isMultiStep), Uid (getUid), GetDetails
// (getDetails), CurrentProgress/TotalProgress (getProgress/getTotalProgress),
// GetStepProgress (getStepProgress), SetProgress (setProgress) and Clear support.
namespace Flint.Core.Tasks;

public enum TaskState
{
    Inactive,
    Running,
    Succeeded,
    Failed,
    AbortedByUser,
}

/// <summary>
/// Represents a task that has to be done. To create a task, subclass this class, implement
/// <see cref="ExecuteAsync"/> and call <see cref="EmitSucceeded"/> or <see cref="EmitFailed"/>
/// when the task is done. The caller needs to call <see cref="StartAsync"/> to start the task.
/// </summary>
public abstract class FlintTask
{
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private long _progress;
    private long _progressTotal = 100;
    private string _details = "";

    protected FlintTask()
    {
        Uid = Guid.NewGuid();
    }

    public TaskState State { get; protected set; } = TaskState.Inactive;

    /// <summary>
    /// The string that was passed to <see cref="EmitFailed"/> as the error message when the task
    /// failed ("Aborted" after an abort). If the task hasn't failed, returns an empty string.
    /// </summary>
    public string FailReason { get; protected set; } = "";

    /// <summary>The last status line set through <see cref="EmitStatus"/>.</summary>
    public string Status { get; protected set; } = "";

    /// <summary>Port of Task::getUid() (QUuid::createUuid()).</summary>
    public Guid Uid { get; }

    public bool IsRunning => State == TaskState.Running;

    public bool IsFinished => State is not TaskState.Running and not TaskState.Inactive;

    /// <summary>Port of Task::wasSuccessful().</summary>
    public bool WasSuccessful => State == TaskState.Succeeded;

    /// <summary>
    /// Port of Task::isMultiStep(): multi-step tasks are combinations of multiple tasks into a
    /// single logical task (the main usage is in SequentialTask).
    /// </summary>
    public virtual bool IsMultiStep => false;

    /// <summary>Port of Task::getProgress().</summary>
    public long CurrentProgress => _progress;

    /// <summary>Port of Task::getTotalProgress().</summary>
    public long TotalProgress => _progressTotal;

    /// <summary>Port of Task::getDetails(). (A Details property would clash with the event.)</summary>
    public string GetDetails() => _details;

    /// <summary>Port of the succeeded() signal.</summary>
    public event Action<FlintTask>? Succeeded;

    /// <summary>Port of the failed(QString) signal.</summary>
    public event Action<FlintTask, string>? Failed;

    /// <summary>Port of the aborted() signal, emitted after abort() was honoured.</summary>
    public event Action<FlintTask>? Aborted;

    /// <summary>Port of the status(QString) signal.</summary>
    public event Action<FlintTask, string>? StatusChanged;

    /// <summary>Port of the details(QString) signal.</summary>
    public event Action<FlintTask, string>? Details;

    /// <summary>Merges the C++ progress(qint64, qint64) and stepProgress(TaskStepProgress) signals.</summary>
    public event Action<FlintTask, TaskStepProgress>? Progress;

    /// <summary>
    /// Port of Task::start(). Transitions the state machine (any end state may be restarted; a
    /// task that is already running is not restarted, the in-flight run is returned instead).
    /// Never throws: an exception from <see cref="ExecuteAsync"/> routes to the terminal states.
    /// The returned System.Threading.Tasks.Task completes when the task reaches a terminal state.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (State == TaskState.Running)
        {
            // Task::start() refuses to start a task that is already running; await the in-flight
            // run so the returned Task still completes at the terminal state.
            await (_runTask ?? Task.CompletedTask).ConfigureAwait(false);
            return;
        }

        // NOTE: only fall through to here in end states (C++ restarts from any end state).
        State = TaskState.Running;
        var previousCts = _cts;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        previousCts?.Dispose();

        var runTask = RunAsync(_cts.Token);
        _runTask = runTask;
        await runTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Port of Task::abort(): asks the task to stop. The request is cooperative — the
    /// <see cref="ExecuteAsync"/> implementation observes the cancellation token; composites
    /// override this to abort their sub-tasks as well.
    /// </summary>
    public virtual void Abort()
    {
        if (State == TaskState.Running)
        {
            _cts?.Cancel();
        }
    }

    /// <summary>
    /// The task subclass must implement this method; it is called to run the task and must call
    /// <see cref="EmitSucceeded"/>, <see cref="EmitFailed"/> or <see cref="EmitAborted"/> when
    /// the task is done (a body that returns without emitting leaves the task Running, exactly
    /// like a C++ executeTask() that never emits).
    /// </summary>
    protected abstract Task ExecuteAsync(CancellationToken cancellationToken);

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            await ExecuteAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation maps to the C++ abort() path: AbortedByUser with reason "Aborted".
            EmitAborted();
        }
        catch (Exception ex)
        {
            // StartAsync never throws; failures route to emitFailed().
            EmitFailed(ex.Message);
        }
    }

    /// <summary>The Task subclass must call this method when the task has succeeded.</summary>
    protected void EmitSucceeded()
    {
        // Don't succeed twice.
        if (!IsRunning)
        {
            return;
        }
        State = TaskState.Succeeded;
        Succeeded?.Invoke(this);
    }

    /// <summary>The Task subclass must call this method when the task has failed.</summary>
    protected void EmitFailed(string reason)
    {
        // Don't fail twice.
        if (!IsRunning)
        {
            return;
        }
        State = TaskState.Failed;
        FailReason = reason;
        Failed?.Invoke(this, reason);
    }

    /// <summary>
    /// The Task subclass must call this method when the task has aborted. External code should
    /// call <see cref="Abort"/> instead.
    /// </summary>
    protected void EmitAborted()
    {
        // Don't abort twice.
        if (!IsRunning)
        {
            return;
        }
        State = TaskState.AbortedByUser;
        FailReason = "Aborted"; // tr("Aborted")
        Aborted?.Invoke(this);
    }

    /// <summary>Port of Task::setStatus(); only emits on change, like the C++ original.</summary>
    protected void EmitStatus(string status)
    {
        if (Status == status)
        {
            return;
        }
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    /// <summary>Port of Task::setDetails(); only emits on change, like the C++ original.</summary>
    protected void EmitDetails(string details)
    {
        if (_details == details)
        {
            return;
        }
        _details = details;
        Details?.Invoke(this, details);
    }

    /// <summary>Raises the Progress event (port of the stepProgress signal); no change guard.</summary>
    protected void EmitProgress(TaskStepProgress progress)
    {
        Progress?.Invoke(this, progress);
    }

    /// <summary>Port of Task::setProgress(current, total); only emits on change.</summary>
    protected void SetProgress(long current, long total)
    {
        if (_progress == current && _progressTotal == total)
        {
            return;
        }
        _progress = current;
        _progressTotal = total;
        EmitProgress(new TaskStepProgress(Uid)
        {
            Current = current,
            Total = total,
            Status = Status,
            Details = _details,
            State = TaskStepState.Running,
        });
    }

    /// <summary>Used by ConcurrentTask::clear() to reset the overall progress (C++: m_progress = 0).</summary>
    protected void ResetProgress()
    {
        _progress = 0;
    }

    /// <summary>
    /// Progress bookkeeping used by ConcurrentTask: the task's own (current, total) progress as a
    /// step progress. Composites override this with the C++ totalProgress()/stepProgress()
    /// aggregation semantics (done subtasks out of the total size).
    /// </summary>
    public virtual TaskStepProgress GetProgress() => new(Uid)
    {
        Current = _progress,
        Total = _progressTotal,
        Status = Status,
        Details = _details,
        State = ToStepState(State),
    };

    /// <summary>Port of Task::getStepProgress(): per-subtask progress; only composites have entries.</summary>
    public virtual IReadOnlyList<TaskStepProgress> GetStepProgress() => [];

    private protected static TaskStepState ToStepState(TaskState state) => state switch
    {
        TaskState.Running => TaskStepState.Running,
        TaskState.Succeeded => TaskStepState.Succeeded,
        TaskState.Inactive => TaskStepState.Waiting,
        _ => TaskStepState.Failed, // Failed and AbortedByUser
    };
}
