// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// What the HTTP task surface needs from a running operation: an id the client can poll, a
// status window it can read from any request thread, and a handle to stop it. The work itself
// is a Task — the window is filled by whoever runs it.

using FlintLauncher.Core.Launch;
using FlintLauncher.Core.Tasks;
using FlintLauncher.Web.Models;

namespace FlintLauncher.Web.Services;

public enum TaskPhase
{
    Pending,
    Running,

    /// <summary>Never stored on the handle: a launch parked at its proceed gate is reported
    /// from the launcher's own state (<see cref="FlintTaskRegistry.StateOf"/>), because the
    /// gate lives on the launch task, not on this window.</summary>
    Waiting,

    Succeeded,
    Failed,
    Aborted,
}

/// <summary>One registered operation: the running work plus the status window the API reads.
/// Every member is safe to read while the work is in flight.</summary>
public sealed class TaskHandle : IProgress<TaskProgress>
{
    private readonly Lock _gate = new();
    private TaskPhase _phase = TaskPhase.Pending;
    private string _status = "";
    private long _current;
    private long _total = 100;
    private string? _failReason;

    public required string Id { get; init; }
    public required string Kind { get; init; }
    public string? InstanceId { get; init; }

    /// <summary>The running work. Set once the operation starts; awaiting it is optional —
    /// the client observes the window, not the task.</summary>
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    public Task? Run { get; set; }

    /// <summary>The token the work observes. Abort() cancels it.</summary>
    public CancellationTokenSource Cancellation { get; } = new();

    /// <summary>Owner-side stop hook, run before the token is cancelled: a launch task kills
    /// the game rather than waiting for its step loop to notice.</summary>
    public Action? Stop { get; init; }

    /// <summary>Launch-only: the ReadyForLaunch gate and the game log model.</summary>
    public LaunchController? Controller { get; init; }

    /// <summary>Terminal payload published for the client.</summary>
    public TaskResultView? Result { get; set; }

    public void SetStatus(string status)
    {
        lock (_gate)
        {
            _status = status;
        }
    }

    public void SetProgress(long current, long total)
    {
        lock (_gate)
        {
            _current = current;
            _total = total;
        }
    }

    // Explicit: the window already exposes Progress as the property the API reads.
    void IProgress<TaskProgress>.Report(TaskProgress value) => SetProgress(value.Current, value.Total);

    /// <summary>Runs one work unit and maps its outcome onto the window: returning succeeds it,
    /// a cancellation aborts it, any other exception fails it with the exception message as the
    /// reason. <paramref name="onSucceeded"/> runs only when the work returned, before the
    /// window turns succeeded, for a payload that exists only then.</summary>
    public async Task RunAsync(Func<CancellationToken, Task> work, Action? onSucceeded = null)
    {
        MarkRunning();
        try
        {
            await work(Cancellation.Token).ConfigureAwait(false);
            onSucceeded?.Invoke();
            Succeed();
        }
        catch (OperationCanceledException)
        {
            MarkAborted();
        }
        catch (Exception e)
        {
            Fail(TaskWork.ReasonOf(e));
        }
    }

    public void Succeed() => Transition(TaskPhase.Succeeded, null);

    public void Fail(string reason) => Transition(TaskPhase.Failed, reason);

    public void MarkAborted() => Transition(TaskPhase.Aborted, "Aborted");

    public void MarkRunning()
    {
        lock (_gate)
        {
            if (!IsTerminalLocked())
            {
                _phase = TaskPhase.Running;
            }
        }
    }

    public TaskPhase Phase
    {
        get
        {
            lock (_gate)
            {
                return _phase;
            }
        }
    }

    public string Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    public (long Current, long Total) Progress
    {
        get
        {
            lock (_gate)
            {
                return (_current, _total);
            }
        }
    }

    /// <summary>Null while the operation is still running (the API only reports a reason for
    /// a terminal operation).</summary>
    public string? FailReason
    {
        get
        {
            lock (_gate)
            {
                return IsTerminalLocked() ? _failReason : null;
            }
        }
    }

    public bool IsTerminal
    {
        get
        {
            lock (_gate)
            {
                return IsTerminalLocked();
            }
        }
    }

    public void Abort()
    {
        Stop?.Invoke();
        Cancellation.Cancel();
    }

    private void Transition(TaskPhase phase, string? reason)
    {
        lock (_gate)
        {
            if (IsTerminalLocked())
            {
                return;
            }

            _phase = phase;
            _failReason = reason;
        }
    }

    private bool IsTerminalLocked() =>
        _phase is TaskPhase.Succeeded or TaskPhase.Failed or TaskPhase.Aborted;
}
