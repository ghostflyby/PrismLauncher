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
// Starts `java ... EntryPoint` and drives the stdin launch protocol (launch script +
// "launch"/"abort"). The step ends when the game exits, so its body parks on the process's
// terminal state.
//
// The gate: once the process is running and the script is written, the step asks for the
// launch gate; Proceed() writes "launch". AbortStep() writes "abort" before the gate is
// answered, and kills the process after it.

using System.Text;
using FlintLauncher.Core.Diagnostics;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Settings;
using Microsoft.Extensions.Logging;
using TruePath;

namespace FlintLauncher.Core.Launch.Steps;

public sealed class LauncherPartLaunch : LaunchStep
{
    /// <summary>Diagnostics logger (flint.launch category). Resolved per call from the
    /// injected factory — a disposed host's factory degrades to silence, never throws.</summary>
    private ILogger Log => Diagnostics.Log.For(_loggerFactory, LogCategories.Launch);

    private readonly LoggedProcess _process;
    private readonly string _installRoot;
    private readonly ILoggerFactory _loggerFactory;
    private readonly LaunchUiHooks? _ui;
    private readonly bool _closeAfterLaunch;
    private bool _closeAfterLaunchTriggered;
    public AuthSession? Session { get; set; }
    public MinecraftTarget? TargetToJoin { get; set; }
    private string _launchScript = "";
    private bool _mayProceed;
    private TaskCompletionSource? _exitSignal;
    private string? _failureReason;

    /// <summary>The injected install root is the only layout this step reads: the
    /// jars/NewLaunch.jar lookup anchor.</summary>
    public LauncherPartLaunch(LaunchTask parent, string installRoot, ILoggerFactory loggerFactory,
        LaunchUiHooks? ui = null) : base(parent)
    {
        _installRoot = installRoot;
        _loggerFactory = loggerFactory;
        _ui = ui;
        _closeAfterLaunch = Variant.AsBool(parent.Instance.Settings.Get("CloseAfterLaunch"));
        _process = new LoggedProcess(
            // UTF-8 is the default charset from Java 18 on (JEP 400)
            parent.Instance.GetJavaLanguageVersion() >= 18 ? Encoding.UTF8 : null,
            loggerFactory);

        _process.Log += OnProcessLog;
        _process.StateChanged += OnState;
    }

    private void OnProcessLog(List<string> lines, MessageLevel level)
    {
        if (_closeAfterLaunch && !_closeAfterLaunchTriggered && LaunchTask.MatchesCloseAfterLaunch(lines))
        {
            _closeAfterLaunchTriggered = true;
            _ui?.CloseAfterLaunch?.Invoke();
        }

        EmitLogLines(lines, level);
    }

    public void SetWorkingDirectory(AbsolutePath workingDirectory) => _process.SetWorkingDirectory(workingDirectory);

    internal override async Task RunAsync(CancellationToken cancellationToken)
    {
        var jarPath = LauncherJars.GetJarPath(LauncherJars.NewLaunchJarName, _installRoot);
        if (jarPath.Length == 0)
        {
            const string reason = "Launcher library could not be found. Please check your installation.";
            EmitLogLine(reason, MessageLevel.Fatal);
            throw new InvalidOperationException(reason);
        }

        var instance = Parent.Instance;

        var legacyJarPath = "";
        if (instance.GetLauncher() == "legacy" || instance.ShouldApplyOnlineFixes())
        {
            legacyJarPath = LauncherJars.GetJarPath(LauncherJars.NewLaunchLegacyJarName, _installRoot);
            if (legacyJarPath.Length == 0)
            {
                const string legacyReason = "Legacy launcher library could not be found. Please check your installation.";
                EmitLogLine(legacyReason, MessageLevel.Fatal);
                throw new InvalidOperationException(legacyReason);
            }
        }

        _launchScript = instance.CreateLaunchScript(Session, TargetToJoin);
        var args = instance.JavaArguments();
        EmitLogLine("Java arguments:\n  " + Parent.CensorPrivateInfo(string.Join(" ", args)) + "\n",
            MessageLevel.Launcher);

        var javaPath = LauncherJars.ResolveExecutable(Variant.AsString(instance.Settings.Get("JavaPath")));

        _process.SetProcessEnvironment(instance.CreateLaunchEnvironment());

        // make detachable - this will keep the process running even if the object is destroyed
        _process.SetDetachable(true);

        var classPath = instance.GetClassPath();
        classPath.Insert(0, new AbsolutePath(jarPath));

        if (legacyJarPath.Length != 0)
        {
            classPath.Insert(0, new AbsolutePath(legacyJarPath));
        }

        args.Add("-Djava.library.path=" + instance.GetNativePath());

        args.Add("-cp");
        args.Add(OperatingSystem.IsWindows() ? string.Join(';', classPath) : string.Join(':', classPath));
        args.Add("org.prismlauncher.EntryPoint");

        var wrapperCommandStr = instance.GetWrapperCommand().Trim();
        // created before Start(): a process that exits immediately reports its terminal state
        // before Start() returns, and that signal must not be lost
        _exitSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (wrapperCommandStr.Length != 0)
        {
            var wrapperArgs = Parent.SubstituteVariables(wrapperCommandStr);
            var wrapperCommand = wrapperArgs[0];
            wrapperArgs.RemoveAt(0);
            var realWrapperCommand = LauncherJars.FindExecutable(wrapperCommand);
            if (realWrapperCommand.Length == 0)
            {
                var reason = $"The wrapper command \"{wrapperCommand}\" couldn't be found.";
                EmitLogLine(reason, MessageLevel.Fatal);
                throw new InvalidOperationException(reason);
            }

            EmitLogLine("Wrapper command is:\n" + wrapperCommandStr + "\n\n", MessageLevel.Launcher);
            args.Insert(0, javaPath);
            _process.Start(wrapperCommand, wrapperArgs.Concat(args).ToList());
        }
        else
        {
            _process.Start(javaPath, args);
        }

        // the step ends when the game exits; the launch gate lives in the child's stdin
        // protocol (Proceed() writes "launch"), not in a wait here
        // the game's exit decides this step, so an abort does not cut the wait short: the
        // process reports how it ended (a crash on the abort command is still a crash), and
        // only then does the step unwind
        await _exitSignal.Task.ConfigureAwait(false);

        // the exit callback records the reason instead of faulting the signal, so a body that
        // already unwound through cancellation leaves no unobserved exception behind
        if (_failureReason is { } failure)
        {
            throw new InvalidOperationException(failure);
        }
    }

    private void OnState(LoggedProcessState state)
    {
        void Fail(string reason, bool log = true)
        {
            _failureReason = reason;
            if (log)
            {
                EmitLogLine(reason, MessageLevel.Fatal);
            }

            _exitSignal?.TrySetResult();
        }

        switch (state)
        {
            case LoggedProcessState.FailedToStart:
                Fail($"Could not launch Minecraft: {_process.ErrorString}");
                return;
            case LoggedProcessState.Aborted:
            case LoggedProcessState.Crashed:
                Parent.Pid = -1;
                Parent.Instance.SetMinecraftRunning(false);
                Fail("Game crashed.", log: false);
                return;
            case LoggedProcessState.Finished:
            {
                var instance = Parent.Instance;

                if (_closeAfterLaunch)
                {
                    _ui?.ShowMainWindow?.Invoke();
                }

                Parent.Pid = -1;
                instance.SetMinecraftRunning(false);
                // if the exit code wasn't 0, report this as a crash
                if (_process.ExitCode != 0)
                {
                    Fail("Game crashed.", log: false);
                    return;
                }

                _exitSignal?.TrySetResult();
                return;
            }
            case LoggedProcessState.Running:
            {
                EmitLogLine($"Minecraft process ID: {_process.ProcessId}\n\n", MessageLevel.Launcher);
                Parent.Pid = _process.ProcessId;
                // send the launch script to the launcher part
                _process.Write(_launchScript);
                // the script write and the gate below are the two events a "game never
                // started" investigation turns on — surface them in both channels
                Log.ScriptWritten(_launchScript.Length);
                EmitLogLine($"Launch script piped to stdin ({_launchScript.Length} chars).", MessageLevel.Launcher);

                _mayProceed = true;
                EmitReadyForLaunch();
                return;
            }
        }
    }

    public override void Proceed()
    {
        if (_mayProceed)
        {
            Parent.Instance.SetMinecraftRunning(true);
            _process.Write("launch\n");
            _mayProceed = false;
            Log.LaunchSignalSent();
            EmitLogLine("Launch signal ('launch') sent to the game process.", MessageLevel.Launcher);
        }
        else
        {
            Log.GateNotArmed();
        }
    }

    public override bool CanAbort() => true;

    public override bool AbortStep()
    {
        if (_mayProceed)
        {
            _mayProceed = false;
            _process.Write("abort\n");
        }
        else
        {
            var state = _process.State;
            if (state is LoggedProcessState.Running or LoggedProcessState.Starting)
            {
                _process.Kill();
            }
        }

        return true;
    }
}
