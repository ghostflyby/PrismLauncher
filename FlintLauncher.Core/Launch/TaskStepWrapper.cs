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
// Wraps a plain async body (the instance update pipeline) as a launch step.
//
// The step asks for the progress gate and starts the body once Proceed() runs, so a host that
// drives the launch from the API can hold it there. The wrapped failure becomes this step's,
// with the pipeline's prefix.

using FlintLauncher.Core.Tasks;

namespace FlintLauncher.Core.Launch;

public sealed class TaskStepWrapper(LaunchTask parent, Func<CancellationToken, Task> task)
    : LaunchStep(parent)
{
    private TaskCompletionSource? _proceedSignal;
    private bool _bodyFinished;

    internal override async Task RunAsync(CancellationToken cancellationToken)
    {
        _proceedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EmitProgressReportingRequest();
        await _proceedSignal.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await task(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            var reason = $"Instance update failed because: {TaskWork.ReasonOf(e)}\n\n";
            EmitLogLine(reason, MessageLevel.Fatal);
            throw new InvalidOperationException(reason, e);
        }
        finally
        {
            _bodyFinished = true;
        }
    }

    public override void Proceed() => _proceedSignal?.TrySetResult();

    /// <summary>A body that already returned has nothing left to stop.</summary>
    public override bool CanAbort() => !_bodyFinished;
}
