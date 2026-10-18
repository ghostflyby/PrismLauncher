// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/launch/TaskStepWrapper.{h,cpp}: wraps a plain FlintTask (the instance
// update pipeline) as a LaunchStep.
//
// Deviation from the C++ original: the C++ executeTask() emits progressReportingRequest() and
// the UI calls proceed() to start the wrapped task; proceed() maps to m_task->start(). The C#
// port keeps the gate — the wrapped task is only started once Proceed() runs — but awaits it
// instead of relying on the finished() signal, so the step completes when the wrapped task
// reaches its terminal state.
// canAbort()/abort() port note: the C++ forwards to the wrapped task's canAbort()/abort()
// (C++ Task::canAbort defaults to a false flag). The C# FlintTask always accepts abort
// requests, so "can abort" here means the wrapped task has not finished yet.

using FlintLauncher.Core.Tasks;

namespace FlintLauncher.Core.Launch;

public sealed class TaskStepWrapper(LaunchTask parent, FlintTask task) : LaunchStep(parent)
{
    private FlintTask? task = task;
    private TaskCompletionSource? proceedSignal;

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (State == TaskState.AbortedByUser)
        {
            EmitFailed("Task aborted.");
            return;
        }

        // progressReportingRequest + the proceed() gate: the wrapped task starts once the
        // controller confirms (headless: auto-proceed).
        proceedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EmitProgressReportingRequest();
        await proceedSignal.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        var task = this.task;
        if (task is null)
        {
            EmitSucceeded();
            return;
        }

        await task.StartAsync(cancellationToken).ConfigureAwait(false);

        if (task.WasSuccessful)
        {
            this.task = null;
            EmitSucceeded();
        }
        else
        {
            var reason = $"Instance update failed because: {task.FailReason}\n\n";
            this.task = null;
            EmitLogLine(reason, MessageLevel.Fatal);
            EmitFailed(reason);
        }
    }

    public override void Proceed() => proceedSignal?.TrySetResult();

    public override bool CanAbort() => task is null || !task.IsFinished;

    public override bool AbortStep()
    {
        if (task is not null && !task.IsFinished)
        {
            task.Abort();
            return true;
        }

        return base.AbortStep();
    }
}
