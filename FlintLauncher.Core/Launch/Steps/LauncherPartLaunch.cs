// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/launch/LauncherPartLaunch.{h,cpp}: the step that actually starts
// `java ... EntryPoint` and drives the stdin launch protocol (launch script + "launch"/"abort").
//
// The C++ step stays unfinished while the game runs; the port parks ExecuteAsync on a
// TaskCompletionSource that the process terminal states complete, so the step (and therefore
// the whole LaunchTask) only finishes when the game exits — exactly the C++ flow.
//
// Deviations from the C++ original:
//  - APPLICATION->getJarPath -> LauncherJars.GetJarPath against Services.Paths.InstallRoot;
//    APPLICATION->closeAllWindows()/showMainWindow() (CloseAfterLaunch) ->
//    Services.Ui.CloseAfterLaunch/ShowMainWindow; the Linux gamemode request
//    (gamemode_request_start_for) is a documented stub (Linux-only, needs libgamemode).
//  - Q_OS_WIN local-8bit path mangling (FS::getPathNameInLocal8bit) is not ported (modern
//    Windows handles UTF-16 command lines; the C++ used it for pre-Windows-10 javaw).
//  - The readyForLaunch()/proceed() gating is modeled with mayProceed state exactly like the
//    C++: the Running state arms `mayProceed` and emits ReadyForLaunch; Proceed() writes
//    "launch\n", AbortStep() writes "abort\n" (or kills).

using System.Text;
using FlintLauncher.Core.Diagnostics;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Settings;
using Microsoft.Extensions.Logging;

namespace FlintLauncher.Core.Launch.Steps;

public sealed class LauncherPartLaunch : LaunchStep
{
    /// <summary>Diagnostics logger (flint.launch category). Resolved per call from the
    /// injected factory — a disposed host's factory degrades to silence, never throws.</summary>
    private ILogger Log => Diagnostics.Log.For(Services.LoggerFactory, LogCategories.Launch);

    private readonly LoggedProcess _process;
    private readonly bool _closeAfterLaunch;
    private bool _closeAfterLaunchTriggered;
    public AuthSession? Session { get; set; }
    public MinecraftTarget? TargetToJoin { get; set; }
    private string _launchScript = "";
    private bool _mayProceed;
    private TaskCompletionSource? _exitSignal;

    public LauncherPartLaunch(LaunchTask parent) : base(parent)
    {
        _closeAfterLaunch = Variant.AsBool(parent.Instance.Settings.Get("CloseAfterLaunch"));
        _process = new LoggedProcess(
            parent.Instance.GetJavaVersion().DefaultsToUtf8() ? Encoding.UTF8 : null,
            parent.Services.LoggerFactory);

        _process.Log += OnProcessLog;
        _process.StateChanged += OnState;
    }

    private void OnProcessLog(List<string> lines, MessageLevel level)
    {
        if (_closeAfterLaunch && !_closeAfterLaunchTriggered && LaunchTask.MatchesCloseAfterLaunch(lines))
        {
            _closeAfterLaunchTriggered = true;
            Services.Ui.CloseAfterLaunch?.Invoke();
        }

        EmitLogLines(lines, level);
    }

    public void SetWorkingDirectory(string workingDirectory) => _process.SetWorkingDirectory(workingDirectory);

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var jarPath = LauncherJars.GetJarPath(LauncherJars.NewLaunchJarName, Services.Paths.InstallRoot);
        if (jarPath.Length == 0)
        {
            const string reason = "Launcher library could not be found. Please check your installation.";
            EmitLogLine(reason, MessageLevel.Fatal);
            EmitFailed(reason);
            return Task.CompletedTask;
        }

        var instance = Parent.Instance;

        var legacyJarPath = "";
        if (instance.GetLauncher() == "legacy" || instance.ShouldApplyOnlineFixes())
        {
            legacyJarPath = LauncherJars.GetJarPath(LauncherJars.NewLaunchLegacyJarName, Services.Paths.InstallRoot);
            if (legacyJarPath.Length == 0)
            {
                const string legacyReason = "Legacy launcher library could not be found. Please check your installation.";
                EmitLogLine(legacyReason, MessageLevel.Fatal);
                EmitFailed(legacyReason);
                return Task.CompletedTask;
            }
        }

        _launchScript = instance.CreateLaunchScript(Session, TargetToJoin, Services);
        var args = instance.JavaArguments();
        var allArgs = string.Join(" ", args);
        EmitLogLine("Java arguments:\n  " + Parent.CensorPrivateInfo(allArgs) + "\n", MessageLevel.Launcher);

        var javaPath = LauncherJars.ResolveExecutable(Variant.AsString(instance.Settings.Get("JavaPath")));

        _process.SetProcessEnvironment(instance.CreateLaunchEnvironment());

        // make detachable - this will keep the process running even if the object is destroyed
        _process.SetDetachable(true);

        var classPath = instance.GetClassPath();
        classPath.Insert(0, jarPath);

        if (legacyJarPath.Length != 0)
        {
            classPath.Insert(0, legacyJarPath);
        }

        var natPath = instance.GetNativePath();
        args.Add("-Djava.library.path=" + natPath);

        args.Add("-cp");
        args.Add(OperatingSystem.IsWindows() ? string.Join(';', classPath) : string.Join(':', classPath));
        args.Add("org.prismlauncher.EntryPoint");

        var wrapperCommandStr = instance.GetWrapperCommand().Trim();
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
                EmitFailed(reason);
                return Task.CompletedTask;
            }

            EmitLogLine("Wrapper command is:\n" + wrapperCommandStr + "\n\n", MessageLevel.Launcher);
            args.Insert(0, javaPath);
            _process.Start(wrapperCommand, wrapperArgs.Concat(args).ToList());
        }
        else
        {
            _process.Start(javaPath, args);
        }

        // (Linux only: gamemode_request_start_for — not ported, see header)

        // park until the process reaches a terminal state (the game exiting)
        _exitSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _exitSignal.Task.WaitAsync(cancellationToken);
    }

    private void OnState(LoggedProcessState state)
    {
        switch (state)
        {
            case LoggedProcessState.FailedToStart:
            {
                var reason = $"Could not launch Minecraft: {_process.ErrorString}";
                EmitLogLine(reason, MessageLevel.Fatal);
                EmitFailed(reason);
                CompleteExit();
                return;
            }
            case LoggedProcessState.Aborted:
            case LoggedProcessState.Crashed:
            {
                Parent.Pid = -1;
                Parent.Instance.SetMinecraftRunning(false);
                EmitFailed("Game crashed.");
                CompleteExit();
                return;
            }
            case LoggedProcessState.Finished:
            {
                var instance = Parent.Instance;

                if (_closeAfterLaunch)
                {
                    Services.Ui.ShowMainWindow?.Invoke();
                }

                Parent.Pid = -1;
                instance.SetMinecraftRunning(false);
                // if the exit code wasn't 0, report this as a crash
                var exitCode = _process.ExitCode;
                if (exitCode != 0)
                {
                    EmitFailed("Game crashed.");
                    CompleteExit();
                    return;
                }

                // FIXME: make this work again
                //  m_postlaunchprocess.processEnvironment().insert("INST_EXITCODE", QString(exitCode));
                //  run post-exit
                EmitSucceeded();
                CompleteExit();
                break;
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
                break;
            }
        }
    }

    private void CompleteExit() => _exitSignal?.TrySetResult();

    public override void Proceed()
    {
        if (_mayProceed)
        {
            Parent.Instance.SetMinecraftRunning(true);
            const string launchString = "launch\n";
            _process.Write(launchString);
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
            const string launchString = "abort\n";
            _process.Write(launchString);
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
