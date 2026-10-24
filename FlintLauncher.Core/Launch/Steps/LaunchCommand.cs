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
// Port of launcher/launch/steps/LaunchCommand.{h,cpp}: run the pre-load/pre-launch/post-exit
// command through a LoggedProcess with the instance environment.

using TruePath;

namespace FlintLauncher.Core.Launch.Steps;

public sealed class LaunchCommand : LaunchStep
{
    private readonly LoggedProcess _process = new();
    private readonly string _command;
    private readonly string _phaseName;

    public LaunchCommand(LaunchTask parent, string command, string phaseName = "") : base(parent)
    {
        _command = command;
        _phaseName = phaseName;
        var instance = Parent.Instance;
        _process.SetProcessEnvironment(instance.CreateEnvironment());
        _process.Log += (lines, level) => EmitLogLines(lines, level);
        _process.StateChanged += OnState;
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var args = Parent.SubstituteVariables(_command);
        EmitLogLine($"Running {_phaseName} command: {string.Join(' ', args)}", MessageLevel.Launcher);
        if (args.Count == 0)
        {
            var emptyError = $"{_phaseName} command is empty, skipping.";
            EmitLogLine(emptyError, MessageLevel.Fatal);
            EmitFailed(emptyError);
            return Task.CompletedTask;
        }

        var program = args[0];
        args.RemoveAt(0);
        _process.Start(program, args);
        return Task.CompletedTask;
    }

    private void OnState(LoggedProcessState state)
    {
        string GetError() => $"{_phaseName} command failed with code {_process.ExitCode}.\n\n";
        switch (state)
        {
            case LoggedProcessState.Aborted:
            case LoggedProcessState.Crashed:
            case LoggedProcessState.FailedToStart:
            {
                var error = GetError();
                EmitLogLine(error, MessageLevel.Fatal);
                EmitFailed(error);
                return;
            }
            case LoggedProcessState.Finished:
            {
                if (_process.ExitCode != 0)
                {
                    var error = GetError();
                    EmitLogLine(error, MessageLevel.Fatal);
                    EmitFailed(error);
                }
                else
                {
                    EmitLogLine($"{_phaseName} command ran successfully.\n\n", MessageLevel.Launcher);
                    EmitSucceeded();
                }

                break;
            }
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
