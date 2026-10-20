// SPDX-License-Identifier: GPL-3.0-only
// Port of the TaskStepState enum and the TaskStepProgress struct from launcher/tasks/Task.{h,cpp}.
// It is a mutable struct on purpose: the C++ code relies on value copies when progress is
// emitted (e.g. ConcurrentTask::subTaskFinished copies the entry out of its map before changing
// the state). The upstream struct in this repository has no operator+, Weight or DoneTreatment
// members, so none were invented here.

namespace FlintLauncher.Core.Tasks;

public enum TaskStepState
{
    Waiting,
    Running,
    Failed,
    Succeeded,
}

/// <summary>Port of the C++ TaskStepProgress struct; the never-read upstream members (the
/// old-current/old-total deltas and the status/details strings) are not ported.</summary>
public struct TaskStepProgress(Guid uid)
{
    public TaskStepProgress()
        : this(Guid.NewGuid())
    {
    }

    public Guid Uid { get; set; } = uid;
    public long Current { get; set; } = 0;
    public long Total { get; set; } = -1;
    public TaskStepState State { get; set; } = TaskStepState.Waiting;

    /// <summary>Port of TaskStepProgress::isDone().</summary>
    public readonly bool IsDone => State is TaskStepState.Failed or TaskStepState.Succeeded;

    /// <summary>Port of TaskStepProgress::update(new_current, new_total).</summary>
    public void Update(long newCurrent, long newTotal)
    {
        Current = newCurrent;
        Total = newTotal;
        State = TaskStepState.Running;
    }
}
