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
