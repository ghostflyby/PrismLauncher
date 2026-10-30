// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022 Sefa Eyeoglu <contact@scrumplex.net>
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
// Runs the pre-load/pre-launch/post-exit command through a LoggedProcess with the instance
// environment. The step ends when the command's process exits, so its body parks on the exit
// signal instead of returning when the process starts.

using TruePath;

namespace FlintLauncher.Core.Launch.Steps;

public sealed class LaunchCommand : AwaitedStep
{
    private readonly LoggedProcess _process = new();
    private readonly string _command;
    private readonly string _phaseName;
    private TaskCompletionSource? _exitSignal;
    private string? _failureReason;

    public LaunchCommand(LaunchTask parent, string command, string phaseName = "") : base(parent)
    {
        _command = command;
        _phaseName = phaseName;
        var instance = Parent.Instance;
        _process.SetProcessEnvironment(instance.CreateEnvironment());
        _process.Log += (lines, level) => EmitLogLines(lines, level);
        _process.StateChanged += OnState;
    }

    protected override async Task RunAsync(CancellationToken cancellationToken)
    {
        var args = Parent.SubstituteVariables(_command);
        var commandLine = string.Join(' ', args);
        EmitLogLine($"Running {_phaseName} command: {commandLine}", MessageLevel.Launcher);
        if (args.Count == 0)
        {
            var emptyError = $"{_phaseName} command is empty, skipping.";
            EmitLogLine(emptyError, MessageLevel.Fatal);
            throw new InvalidOperationException(emptyError);
        }

        _exitSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var program = args[0];
        args.RemoveAt(0);
        _process.Start(program, args);
        await _exitSignal.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        // the exit callback records the reason instead of faulting the signal, so a body that
        // already unwound through cancellation leaves no unobserved exception behind
        if (_failureReason is { } failure)
        {
            throw new InvalidOperationException(failure);
        }
    }

    private void OnState(LoggedProcessState state)
    {
        string Error() => $"{_phaseName} command failed with code {_process.ExitCode}.\n\n";

        void Fail(string reason)
        {
            _failureReason = reason;
            EmitLogLine(reason, MessageLevel.Fatal);
            _exitSignal?.TrySetResult();
        }

        switch (state)
        {
            case LoggedProcessState.Aborted:
            case LoggedProcessState.Crashed:
            case LoggedProcessState.FailedToStart:
                Fail(Error());
                return;
            case LoggedProcessState.Finished:
                if (_process.ExitCode != 0)
                {
                    Fail(Error());
                }
                else
                {
                    EmitLogLine($"{_phaseName} command ran successfully.\n\n", MessageLevel.Launcher);
                    _exitSignal?.TrySetResult();
                }

                break;
        }
    }

    public void SetWorkingDirectory(AbsolutePath workingDirectory) => _process.SetWorkingDirectory(workingDirectory);

    public override bool CanAbort() => true;

    public override bool AbortStep()
    {
        var state = _process.State;
        if (state is LoggedProcessState.Running or LoggedProcessState.Starting)
        {
            _process.Kill();
        }

        return true;
    }
}
