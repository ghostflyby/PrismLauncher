// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022 flowln <flowlnlnln@gmail.com>
// SPDX-FileCopyrightText: 2023 Rachel Powers <508861+Ryex@users.noreply.github.com>
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
public class SequentialTask(string taskName = "") : ConcurrentTask(taskName, 1)
{
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
        lock (Gate)
        {
            stamp = BeginStateUpdateLocked();
            doing = Doing.Count;
            done = Done.Count;
            totalSize = TotalSizeLocked();
        }

        EmitStateUpdate(stamp, () =>
        {
            SetProgress(done, totalSize);
            EmitStatus($"Executing task {doing + done} out of {totalSize}");
        });
    }
}
