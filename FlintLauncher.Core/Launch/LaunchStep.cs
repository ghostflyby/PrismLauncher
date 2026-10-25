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
// Port of launcher/launch/LaunchStep.{h,cpp}.
//
// A LaunchStep is one stage of the LaunchTask pipeline. Like every FlintTask it becomes
// finished by calling EmitSucceeded/EmitFailed/EmitAborted from ExecuteAsync; the LaunchTask
// listens for the terminal states (the C++ `finished` signal) and advances the chain.
//
// Deviations from the C++ original:
//  - The Qt connect() wiring of the constructor becomes C# event subscriptions in the base
//    constructor (readyForLaunch -> LaunchTask.OnReadyForLaunch, logLine/logLines ->
//    OnLogLine/OnLogLines, progressReportingRequest -> OnProgressReportingRequested and the
//    finished() -> onStepFinished() wiring, here expressed as subscriptions to the
//    Succeeded/Failed/Aborted events).
//  - Task::abort() (bool) is ported as AbortStep() (bool); the inherited
//    FlintTask.Abort() (void, cooperative cancellation) is what the default implementation
//    drives, mirroring the C++ Task subclasses that emit aborted() themselves.
//  - `finalize()` is named FinalizeStep() (System.Object::Finalize clash).
//  - C++ Task::canAbort() defaults to a false m_can_abort flag; steps that can be aborted
//    override it. The port keeps that default.

using FlintLauncher.Core.Tasks;

namespace FlintLauncher.Core.Launch;

public abstract class LaunchStep : FlintTask
{
    protected LaunchStep(LaunchTask parent)
    {
        Parent = parent;

        // LaunchStep::LaunchStep() connect() block
        ReadyForLaunch += parent.OnReadyForLaunch;
        LogLine += parent.OnLogLine;
        LogLines += parent.OnLogLines;
        ProgressReportingRequest += parent.OnProgressReportingRequested;
        Succeeded += parent.OnStepFinished;
        Failed += parent.OnStepFailed;
        Aborted += parent.OnStepFinished;
    }

    /// <summary>The LaunchTask this step belongs to (C++ m_parent).</summary>
    public LaunchTask Parent { get; }

    /// <summary>The injected launch dependencies (the parent's LaunchServices): paths, logger
    /// factory, global settings, UI hooks. Replaces the former APPLICATION-singleton static
    /// hooks of the C++ steps.</summary>
    public LaunchServices Services => Parent.Services;

    /// <summary>Port of the logLines(QStringList, MessageLevel) signal.</summary>
    public event Action<List<string>, MessageLevel>? LogLines;

    /// <summary>Port of the logLine(QString, MessageLevel) signal.</summary>
    public event Action<string, MessageLevel>? LogLine;

    /// <summary>Port of the readyForLaunch() signal.</summary>
    public event Action? ReadyForLaunch;

    /// <summary>Port of the progressReportingRequest() signal.</summary>
    public event Action? ProgressReportingRequest;

    protected void EmitLogLines(IReadOnlyList<string> lines, MessageLevel level) =>
        LogLines?.Invoke([.. lines], level);

    protected void EmitLogLine(string line, MessageLevel level) => LogLine?.Invoke(line, level);

    protected void EmitReadyForLaunch() => ReadyForLaunch?.Invoke();

    protected void EmitProgressReportingRequest() => ProgressReportingRequest?.Invoke();

    /// <summary>
    /// Port of the proceed() slot: called when the UI (or the headless controller) confirms the
    /// launch. Steps that gate on user confirmation override this.
    /// </summary>
    public virtual void Proceed() { }

    /// <summary>
    /// Port of the finalize() slot: called in the opposite order than the start, used to clean
    /// up or otherwise undo things after the launch ends.
    /// </summary>
    public virtual void FinalizeStep() { }

    /// <summary>Port of Task::canAbort() (default: not abortable, like the C++ m_can_abort).</summary>
    public virtual bool CanAbort() => false;

    /// <summary>Port of Task::abort() (bool): request the step to stop. Returns whether the
    /// request was honoured.</summary>
    public virtual bool AbortStep()
    {
        Abort();
        return true;
    }
}
