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
// One stage of the LaunchTask pipeline. A step is a body that the engine awaits: returning
// succeeds it and throwing fails it, with the exception's message as the reason. A step whose
// end is an external event (the game exiting) parks its body on that event.
//
// The base constructor wires the step's log and gate requests to the engine, so a step reports
// its log lines and asks for the launch gate without holding the engine reference itself.
//
// abort() is a request: CanAbort() says whether the step can honour one right now, and
// AbortStep() does it (writing an abort command, killing the process). A step that cannot be
// interrupted mid-work reports false rather than pretending.

namespace FlintLauncher.Core.Launch;

public abstract class LaunchStep
{
    protected LaunchStep(LaunchTask parent)
    {
        Parent = parent;

        LogLine += parent.OnLogLine;
        LogLines += parent.OnLogLines;
        ReadyForLaunch += () => parent.OnReadyForLaunch(this);
        ProgressReportingRequest += () => parent.OnProgressReportingRequested(this);
    }

    /// <summary>The LaunchTask this step belongs to.</summary>
    public LaunchTask Parent { get; }

    /// <summary>The injected launch dependencies (the parent's LaunchServices): paths, logger
    /// factory, global settings, UI hooks.</summary>
    public LaunchServices Services => Parent.Services;

    /// <summary>The log lines this step emits, as a list.</summary>
    public event Action<List<string>, MessageLevel>? LogLines;

    /// <summary>One log line from this step.</summary>
    public event Action<string, MessageLevel>? LogLine;

    /// <summary>The step is ready for the launch gate: the host answers with Proceed().</summary>
    public event Action? ReadyForLaunch;

    /// <summary>The step asked the host to make a progress decision (the launch gate).</summary>
    public event Action? ProgressReportingRequest;

    /// <summary>The step's work. Returning succeeds the step; throwing fails it. The token is
    /// cancelled when the launch is aborted, so a body that awaits it stops.</summary>
    internal abstract Task RunAsync(CancellationToken cancellationToken);

    protected void EmitLogLines(IReadOnlyList<string> lines, MessageLevel level) =>
        LogLines?.Invoke([.. lines], level);

    protected void EmitLogLine(string line, MessageLevel level) => LogLine?.Invoke(line, level);

    protected void EmitReadyForLaunch() => ReadyForLaunch?.Invoke();

    protected void EmitProgressReportingRequest() => ProgressReportingRequest?.Invoke();

    /// <summary>Opens the gate this step asked for. Steps that gate override this.</summary>
    public virtual void Proceed() { }

    /// <summary>Runs after the launch chain ends, deepest step first, to undo what the step
    /// set up (an extracted folder, a synthesized jar).</summary>
    public virtual void FinalizeStep() { }

    /// <summary>Whether the step can honour an abort right now. A step that cannot be
    /// interrupted mid-work reports false, the default.</summary>
    public virtual bool CanAbort() => false;

    /// <summary>Requests the step to stop, returning whether the request was honoured. The
    /// engine cancels the launch token when any step honours one, which ends a body parked on
    /// it; a step driving something the token does not reach (a child process) also stops that
    /// here.</summary>
    public virtual bool AbortStep() => CanAbort();
}
