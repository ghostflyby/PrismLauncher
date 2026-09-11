// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/tasks/MultipleOptionsTask.{h,cpp}. This task type will attempt to run each of
// its subtasks in sequence, until one of them succeeds. When that happens, the remaining tasks
// will not run. If all attempts fail, the parent fails with "All attempts have failed!" (not
// with the subtasks' own reasons).
namespace Flint.Core.Tasks;

/// <summary>
/// This task type will attempt to do run each of it's subtasks in sequence, until one of them
/// succeeds. When that happens, the remaining tasks will not run.
/// </summary>
public class MultipleOptionsTask : ConcurrentTask
{
    public MultipleOptionsTask(string taskName = "")
        : base(taskName, 1)
    {
    }

    protected override void ExecuteNextSubTask()
    {
        // Port of MultipleOptionsTask::executeNextSubTask(). Like the C++ original, this has no
        // isRunning() guard of its own — the emit guards cover a task that is already terminal.
        // m_done counts finished subtasks of either kind while m_failed only counts failures:
        // done != failed therefore means one attempt succeeded.
        int done, failed, queued;
        lock (_gate)
        {
            done = _done.Count;
            failed = _failed.Count;
            queued = _queue.Count;
        }

        if (done != failed)
        {
            EmitSucceeded();
            return;
        }

        if (queued == 0)
        {
            EmitFailed("All attempts have failed!");
            return;
        }

        base.ExecuteNextSubTask();
    }

    protected override void UpdateState()
    {
        // Port of MultipleOptionsTask::updateState().
        int doing, done, totalSize;
        lock (_gate)
        {
            doing = _doing.Count;
            done = _done.Count;
            totalSize = TotalSizeLocked();
        }

        SetProgress(done, totalSize);
        EmitStatus($"Attempting task {doing + done} out of {totalSize}");
    }
}
