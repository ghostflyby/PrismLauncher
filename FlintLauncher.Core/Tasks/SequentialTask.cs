// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/tasks/SequentialTask.{h,cpp}. A ConcurrentTask with a single concurrency
// slot: subtasks run one by one, and the first failure fails the whole task right away so the
// remaining subtasks never start (the follow-up executeNextSubTask() sees a non-running parent).
namespace FlintLauncher.Core.Tasks;

/// <summary>
/// A concurrent task that only allows one concurrent task :)
/// This should be used when there's a need to maintain a strict ordering of task executions, and
/// the starting of a task is contingent on the success of the previous one.
/// See <see cref="MultipleOptionsTask"/> if that's not the case.
/// </summary>
public class SequentialTask : ConcurrentTask
{
    public SequentialTask(string taskName = "")
        : base(taskName, 1)
    {
    }

    protected override void SubTaskFailed(FlintTask task, string msg)
    {
        // Port of SequentialTask::subTaskFailed(): record the failure like ConcurrentTask does,
        // then fail this task immediately. The follow-up ExecuteNextSubTask() is a no-op because
        // the task is no longer running, so the remaining subtasks never start.
        SubTaskFinished(task, TaskStepState.Failed);
        EmitFailed(msg);
        ExecuteNextSubTask();
    }

    protected override void UpdateState()
    {
        // Port of SequentialTask::updateState() — stamped like ConcurrentTask's: a snapshot
        // captured under the gate is emitted only while it is still the freshest, so
        // interleaved completion bookkeeping can never leave a stale status behind.
        long stamp;
        int doing, done, totalSize;
        lock (_gate)
        {
            stamp = BeginStateUpdateLocked();
            doing = _doing.Count;
            done = _done.Count;
            totalSize = TotalSizeLocked();
        }

        EmitStateUpdate(stamp, () =>
        {
            SetProgress(done, totalSize);
            EmitStatus($"Executing task {doing + done} out of {totalSize}");
        });
    }
}
