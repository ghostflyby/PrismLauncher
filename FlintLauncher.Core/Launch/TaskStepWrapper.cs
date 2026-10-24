// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// This file incorporates work covered by the following copyright and
// permission notice:
//
//     Copyright 2013-2021 MultiMC Contributors
//
//     Licensed under the Apache License, Version 2.0 (the "License");
//     you may not use this file except in compliance with the License.
//     You may obtain a copy of the License at
//
//         http://www.apache.org/licenses/LICENSE-2.0
//
//     Unless required by applicable law or agreed to in writing, software
//     distributed under the License is distributed on an "AS IS" BASIS,
//     WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//     See the License for the specific language governing permissions and
//     limitations under the License.
//
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
    private FlintTask? _task = task;
    private TaskCompletionSource? _proceedSignal;

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (State == TaskState.AbortedByUser)
        {
            EmitFailed("Task aborted.");
            return;
        }

        // progressReportingRequest + the proceed() gate: the wrapped task starts once the
        // controller confirms (headless: auto-proceed).
        _proceedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EmitProgressReportingRequest();
        await _proceedSignal.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        var wrapped = this._task;
        if (wrapped is null)
        {
            EmitSucceeded();
            return;
        }

        await wrapped.StartAsync(cancellationToken).ConfigureAwait(false);

        if (wrapped.WasSuccessful)
        {
            this._task = null;
            EmitSucceeded();
        }
        else
        {
            var reason = $"Instance update failed because: {wrapped.FailReason}\n\n";
            this._task = null;
            EmitLogLine(reason, MessageLevel.Fatal);
            EmitFailed(reason);
        }
    }

    public override void Proceed() => _proceedSignal?.TrySetResult();

    public override bool CanAbort() => _task is null || !_task.IsFinished;

    public override bool AbortStep()
    {
        if (_task is not null && !_task.IsFinished)
        {
            _task.Abort();
            return true;
        }

        return base.AbortStep();
    }
}
