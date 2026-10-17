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

/// <summary>Port of the C++ TaskStepProgress struct (same fields and semantics).</summary>
public struct TaskStepProgress
{
    public TaskStepProgress()
        : this(Guid.NewGuid()) { }

    public TaskStepProgress(Guid uid)
    {
        Uid = uid;
        Current = 0;
        Total = -1;
        OldCurrent = 0;
        OldTotal = -1;
        Status = "";
        Details = "";
        State = TaskStepState.Waiting;
    }

    public Guid Uid { get; set; }
    public long Current { get; set; }
    public long Total { get; set; }
    public long OldCurrent { get; set; }
    public long OldTotal { get; set; }
    public string Status { get; set; }
    public string Details { get; set; }
    public TaskStepState State { get; set; }

    /// <summary>Port of TaskStepProgress::isDone().</summary>
    public readonly bool IsDone => State is TaskStepState.Failed or TaskStepState.Succeeded;

    /// <summary>Port of TaskStepProgress::update(new_current, new_total).</summary>
    public void Update(long newCurrent, long newTotal)
    {
        OldCurrent = Current;
        OldTotal = Total;

        Current = newCurrent;
        Total = newTotal;
        State = TaskStepState.Running;
    }
}
