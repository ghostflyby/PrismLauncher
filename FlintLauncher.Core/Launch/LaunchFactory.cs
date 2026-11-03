// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

using FlintLauncher.Core.Auth;
using FlintLauncher.Core.Config;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Java;
using FlintLauncher.Core.Launch.Steps;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Settings;
using Microsoft.Extensions.Logging;
using TruePath;

namespace FlintLauncher.Core.Launch;

/// <summary>The launch pipeline's composition point: the constructor-injected globals are
/// the environment the C++ steps reached through the APPLICATION singleton. Nullable
/// members degrade as documented on the consuming steps; <paramref name="options"/>
/// defaults to the gates-off record.</summary>
/// <param name="paths">The store layout, split into the per-consumer paths at creation.</param>
/// <param name="loggerFactory">The host diagnostics factory.</param>
/// <param name="http">The client the runtime archive download rides.</param>
/// <param name="ui">The GUI touchpoints; unset keeps the headless defaults.</param>
/// <param name="config">The typed flint.json store (the java/download switches).</param>
/// <param name="dirs">The dir-kind settings: the JavaDir resolution for the java root, and
/// the managed-versions javalist scans that root — both resolve per launch.</param>
/// <param name="auth">The auth environment for the pre-launch account refresh.</param>
public sealed class LaunchFactory(FlintPaths paths, ILoggerFactory loggerFactory, HttpClient http,
    LaunchOptions? options = null, LaunchUiHooks? ui = null, FlintConfigStore? config = null,
    ConfiguredDirs? dirs = null, AuthEndpointsOptions? auth = null)
{
    /// <summary>The host-level toggles, normalized to the gates-off record.</summary>
    private readonly LaunchOptions _options = options ?? new LaunchOptions();

    /// <summary>Port of createLaunchTask(): builds the <see cref="LaunchTask"/> and its full
    /// step chain for one launch, and invokes the UI's saveIcon (C++
    /// APPLICATION->icons()->saveIcon — the former SaveIconHook static).</summary>
    public LaunchTask CreateTask(MinecraftInstance instance, AuthSession? session,
        MinecraftTarget? targetToJoin)
    {
        instance.UpdateRuntimeContext();
        var process = new LaunchTask(instance, loggerFactory, _options.XmlLogParsing);

        ui?.SaveIcon?.Invoke(instance.IconKey(), instance.GameRoot / "icon.png");

        // print a header
        process.AppendStep(new TextPrint(process, "Minecraft folder is:\n  " + instance.GameRoot + "\n",
            MessageLevel.Launcher));

        // create the .minecraft folder and server-resource-packs (workaround for Minecraft bug MCL-3732)
        process.AppendStep(new CreateGameFolders(process));

        if (targetToJoin is null && Variant.AsBool(instance.Settings.Get("JoinServerOnLaunch")))
        {
            var fullAddress = Variant.AsString(instance.Settings.Get("JoinServerOnLaunchAddress"));
            if (fullAddress.Length != 0)
            {
                targetToJoin = MinecraftTarget.Parse(fullAddress, false);
            }
            else
            {
                var world = Variant.AsString(instance.Settings.Get("JoinWorldOnLaunch"));
                if (world.Length != 0)
                {
                    targetToJoin = MinecraftTarget.Parse(world, true);
                }
            }
        }

        if (targetToJoin is not null && targetToJoin.Port == 25565)
        {
            // Resolve server address to join on launch
            var step = new LookupServerAddress(process, targetToJoin)
            {
                LookupAddress = targetToJoin.Address
            };
            process.AppendStep(step);
        }

        // run pre-load command if that's needed, before the metadata is loaded
        if (instance.GetPreLoadCommand().Length != 0)
        {
            var step = new LaunchCommand(process, instance.GetPreLoadCommand(), "Pre-Load");
            step.SetWorkingDirectory(instance.GameRoot);
            process.AppendStep(step);
        }

        // load meta
        {
            var mode = session?.LaunchMode != LaunchMode.Offline ? NetMode.Online : NetMode.Offline;
            process.AppendStep(new TaskStepWrapper(process,
                cancellationToken => MinecraftLoadAndCheck.RunAsync(instance, mode, cancellationToken)));
        }

        // check java
        {
            process.AppendStep(CreateAutoInstallJava(process));
            process.AppendStep(new CheckJava(process));
            // verify that minimum Java requirements are met
            process.AppendStep(new VerifyJavaInstall(process));
        }

        // run pre-launch command if that's needed
        if (instance.GetPreLaunchCommand().Length != 0)
        {
            var step = new LaunchCommand(process, instance.GetPreLaunchCommand(), "Pre-Launch");
            step.SetWorkingDirectory(instance.GameRoot);
            process.AppendStep(step);
        }

        // if we aren't in offline mode
        if (session?.LaunchMode != LaunchMode.Offline)
        {
            process.AppendStep(new ClaimAccount(process));
            foreach (var t in instance.CreateUpdateTask(_options.ResourceUrlOverride))
            {
                process.AppendStep(new TaskStepWrapper(process, t));
            }
        }
        else
        {
            process.AppendStep(new EnsureOfflineLibraries(process, instance));
        }

        // if there are any jar mods
        process.AppendStep(new ModMinecraftJar(process));

        // Scan mods folders for mods
        process.AppendStep(new ScanModFolders(process));

        // make sure we have enough RAM, warn the user if we don't
        process.AppendStep(CreateEnsureAvailableMemory(process));

        // print some instance info here...
        process.AppendStep(new PrintInstanceInfo(process, session, targetToJoin));

        // extract native jars if needed
        process.AppendStep(new ExtractNatives(process));

        // reconstruct assets if needed
        process.AppendStep(new ReconstructAssets(process));

        {
            // actually launch the game
            var step = CreateLauncherPartLaunch(process);
            step.SetWorkingDirectory(instance.GameRoot);
            step.Session = session;
            step.TargetToJoin = targetToJoin;
            process.AppendStep(step);
        }

        // run post-exit command if that's needed
        if (instance.GetPostExitCommand().Length != 0)
        {
            var step = new LaunchCommand(process, instance.GetPostExitCommand(), "Post-Launch");
            step.SetWorkingDirectory(instance.GameRoot);
            process.AppendStep(step);
        }

        if (session is not null)
        {
            process.SetCensorFilter(instance.CreateCensorFilterFromSession(session));
        }

        if (Variant.AsBool(instance.Settings.Get("QuitAfterGameStop")))
        {
            process.AppendStep(CreateQuitAfterGameStop(process));
        }

        instance.SetLaunchTask(process);
        return process;
    }

    /// <summary>Builds the headless launch controller for one launch: the controller carries
    /// the factory's auth environment and diagnostics logger, and reads the offline name
    /// through the instance's launcher runtime state. The optional dialog hook stands in for
    /// the C++ name dialog (unset: the headless default decides).</summary>
    public LaunchController CreateController(MinecraftInstance instance, LaunchMode wantedMode,
        string offlineName, MinecraftAccount? accountToUse, Func<AccountList?>? accountsProvider,
        bool autoProceed, MinecraftTarget? targetToJoin,
        Func<string, string, string, string?>? askOfflineName = null) =>
        new(loggerFactory, auth, this)
        {
            Instance = instance,
            WantedLaunchMode = wantedMode,
            OfflineName = offlineName,
            AccountToUse = accountToUse,
            AccountsProvider = accountsProvider,
            AutoProceed = autoProceed,
            TargetToJoin = targetToJoin,
            AskOfflineNameHandler = askOfflineName,
        };

    /// <summary>The game-running step: starts `java ... EntryPoint` and drives the stdin
    /// launch protocol. Public for hosts that build partial chains.</summary>
    public LauncherPartLaunch CreateLauncherPartLaunch(LaunchTask parent) =>
        new(parent, paths.InstallRoot, loggerFactory, ui);

    /// <summary>The runtime-pick/install step. Public for hosts that build partial chains.
    /// The java root and the managed-versions javalist resolve here, at step creation: the
    /// JavaDir setting under store semantics (falling back to the store/java anchor), and a
    /// fresh javalist over that root so a launch never reuses a previous launch's detection
    /// state.</summary>
    public AutoInstallJava CreateAutoInstallJava(LaunchTask parent) =>
        new(parent, http, dirs?.JavaRoot ?? paths.JavaRoot, config,
            dirs is null ? null : new JavaInstallList(javaRoot: dirs.JavaRoot));

    /// <summary>The memory-pressure gate. Public for hosts that build partial chains.</summary>
    public EnsureAvailableMemory CreateEnsureAvailableMemory(LaunchTask parent) =>
        new(parent, ui);

    /// <summary>The quit-after-game-stop step. Public for hosts that build partial chains.</summary>
    public QuitAfterGameStop CreateQuitAfterGameStop(LaunchTask parent) =>
        new(parent, ui);
}
