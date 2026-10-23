// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/LaunchController.{h,cpp} (plus launcher/LaunchMode.h, which lives in
// Instances/AuthSession.cs next to AuthSession).
//
// The controller decides the account and the launch mode (Normal/Offline/Demo), fills the
// AuthSession, builds the LaunchTask (prepending the mode/version TextPrint steps and the
// PrintServers step) and runs it to completion — the C++ controller task only finishes when
// the game exits, and so does the ported one (the terminal emissions come from the launcher).
//
// Deviations from the C++ original (dialogs are UI):
//  - decideAccount(): the "no accounts" info box and the ProfileSelectDialog are not ported;
//    the instance account (or the default account) is used as-is. A fresh selection can be
//    injected through <see cref="AccountToUse"/> (the C++ setAccountToUse()).
//  - askPlayDemo(): the demo confirmation box is not ported — the C++ default button
//    (Cancel) wins, so a decided Demo launch without the explicit demo mode aborts.
//  - askOfflineName(): the name dialog becomes <see cref="AskOfflineNameHandler"/>; the
//    headless default uses <see cref="OfflineName"/> and falls back to the suggested name.
//    The C++ stores the choice in APPLICATION->settings() "LastOfflinePlayerName"; the port
//    mirrors that through the launcher runtime state (launcher-state.json — the
//    moved the key out of settings) when one is installed, and the dialog suggestion
//    prefers the stored name like the C++.
//  - reauthenticateAccount(): the re-login dialog is not ported — unset behavior (abort)
//    is the only path.
//  - the ProfileSetupDialog branch (Normal launch without a profile) aborts the launch —
//    there is no dialog to collect a new profile name in the headless core.
//  - readyForLaunch(): the profiler machinery (BaseProfilerFactory) is UI and not ported; the
//    controller auto-proceeds unless <see cref="AutoProceed"/> is turned off for API use.
//  - onProgressRequested(): the progress dialog collapses into an immediate proceed() (the
//    TaskStepWrapper gate must open for the launch to continue).
//  - abort(): the "Kill Minecraft?" confirmation is not ported — the C++ default button
//    (Yes) wins and the game is killed right away.
//  - reloadSettings() and the console-window plumbing (ShowConsole/ShowConsoleOnError) are
//    UI-facing and are not ported.

using System.Diagnostics;
using FlintLauncher.Core.Auth;
using FlintLauncher.Core.Diagnostics;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Java;
using FlintLauncher.Core.Launch.Steps;
using FlintLauncher.Core.Settings;
using FlintLauncher.Core.Tasks;
using Microsoft.Extensions.Logging;

namespace FlintLauncher.Core.Launch;

/// <summary>Port of LaunchDecision.</summary>
public enum LaunchDecision
{
    Undecided,
    Continue,
    Abort,
}

public class LaunchController : FlintTask
{
    private AuthSession? _session;
    private LaunchTask? _launcher;
    private LaunchMode _actualLaunchMode = LaunchMode.Normal;

    /// <summary>The injected launch dependencies: the controller reads/writes the offline
    /// name through <see cref="LaunchServices.LauncherState"/> (launcher-state.json),
    /// its diagnostics logger from <see cref="LaunchServices.LoggerFactory"/> and hands the
    /// services down to the LaunchTask it builds. Null keeps the headless defaults (null
    /// decides like the former unset hooks).</summary>
    public LaunchServices? Services { get; init; }

    /// <summary>Diagnostics logger (flint.controller category). Resolved per call from the
    /// injected factory; null/unset or disposed factories degrade to silence.</summary>
    private ILogger Log => Diagnostics.Log.For(Services?.LoggerFactory, LogCategories.Controller);

    public MinecraftInstance? Instance { get; init; }

    public LaunchMode WantedLaunchMode { get; init; } = LaunchMode.Normal;

    /// <summary>The offline/demo player name (C++ setOfflineName() + the offline-name dialog).</summary>
    public string OfflineName { get; init; } = "";

    /// <summary>C++ setAccountToUse(): pin the account instead of the decideAccount() lookup.</summary>
    public MinecraftAccount? AccountToUse { get; set; }

    /// <summary>C++ APPLICATION->accounts(); null behaves like an empty account list.</summary>
    public Func<AccountList?>? AccountsProvider { get; init; }

    /// <summary>C++ setTargetToJoin(): join this server/world instead of the instance setting.</summary>
    public MinecraftTarget? TargetToJoin { get; init; }

    /// <summary>The launch mode the session ended up with (decideLaunchMode result).</summary>
    public LaunchMode ActualLaunchMode => _actualLaunchMode;

    /// <summary>The built launch task once the session was decided (C++ m_launcher).</summary>
    public LaunchTask? Launcher => _launcher;

    public AuthSession? Session => _session;

    /// <summary>
    /// The C++ askOfflineName() dialog: receives (dialog title, message, suggested name) and
    /// returns the chosen name, or null when cancelled. Unset uses <see cref="OfflineName"/>
    /// (or the suggested name when that is empty) and never cancels.
    /// </summary>
    public Func<string, string, string, string?>? AskOfflineNameHandler { get; init; }

    /// <summary>
    /// Whether ReadyForLaunch is answered with Proceed() automatically (the profiler-less
    /// default). Turn this off to gate the launch from the API; the consumer then subscribes
    /// to <see cref="Launch.LaunchTask.ReadyForLaunch"/> and calls Proceed() itself.
    /// </summary>
    public bool AutoProceed { get; init; } = true;

    // ------------------------------------------------------------------ Task (executeTask)

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (Instance is not { } instance)
        {
            EmitFailed("No instance specified!");
            return;
        }

        if (!JavaCommon.CheckJvmArgs(Variant.AsString(instance.Settings.Get("JvmArgs"))))
        {
            EmitFailed("Invalid Java arguments specified. Please fix this first.");
            return;
        }

        await Login(instance, cancellationToken).ConfigureAwait(false);
    }

    // ----------------------------------------------------------------------- account/login

    /// <summary>Port of decideAccount(): pick the account for the launch.</summary>
    private void DecideAccount(MinecraftInstance instance)
    {
        if (AccountToUse is not null)
        {
            return;
        }

        // Select the account to use. If the instance has a specific account set, that will be
        // used. Otherwise, the default account will be used.
        var accounts = AccountsProvider?.Invoke();
        if (accounts is null)
        {
            return;
        }

        var instanceAccountId = Variant.AsString(instance.Settings.Get("InstanceAccountId"));
        var instanceAccountIndex = accounts.FindAccountByProfileId(instanceAccountId);
        if (instanceAccountIndex == -1 || instanceAccountId.Length == 0)
        {
            AccountToUse = accounts.DefaultAccount;
        }
        else
        {
            AccountToUse = accounts.At(instanceAccountIndex);
        }

        // (the "no accounts" hint and the account picker are UI; see the header note)
    }

    /// <summary>Port of decideLaunchMode() (async form: the refresh flow is awaited).</summary>
    private async Task<LaunchDecision> DecideLaunchMode(CancellationToken cancellationToken)
    {
        if (AccountToUse is null || WantedLaunchMode == LaunchMode.Demo)
        {
            // Headless extension over C++: the API can request an explicit offline launch
            // (WantedLaunchMode.Offline) without any account; C++ only ever reaches Offline
            // as a *downgrade* and otherwise forces Demo when no account exists.
            _actualLaunchMode = AccountToUse is null && WantedLaunchMode == LaunchMode.Offline
                ? LaunchMode.Offline
                : LaunchMode.Demo;
            return LaunchDecision.Continue;
        }

        AccountList? accounts = AccountsProvider?.Invoke();
        MinecraftAccount? accountToCheck;

        if (AccountToUse.AccountType != AccountType.Offline)
        {
            accountToCheck = AccountToUse.OwnsMinecraft() ? AccountToUse : null;
        }
        else if (accounts?.DefaultAccount is { } defaultAccount && defaultAccount.OwnsMinecraft())
        {
            accountToCheck = defaultAccount;
        }
        else
        {
            accountToCheck = null;
            if (accounts is not null)
            {
                for (var i = 0; i < accounts.Count; i++)
                {
                    if (accounts.At(i).OwnsMinecraft())
                    {
                        accountToCheck = accounts.At(i);
                        break;
                    }
                }
            }
        }

        if (accountToCheck is null)
        {
            _actualLaunchMode = LaunchMode.Demo;
            return LaunchDecision.Continue;
        }

        var state = accountToCheck.AccountState;
        var needsRefresh = WantedLaunchMode == LaunchMode.Normal &&
                           (state == AccountState.Offline || accountToCheck.ShouldRefresh());
        if (state is AccountState.Unchecked or AccountState.Errored || needsRefresh)
        {
            // MinecraftAccount::refresh(): start the auth flow and wait for the state to settle
            var refresh = accountToCheck.Refresh();
            if (refresh is not null && !refresh.IsFinished)
            {
                await refresh.StartAsync(cancellationToken).ConfigureAwait(false);
            }

            state = accountToCheck.AccountState;
        }

        if (state == AccountState.Working)
        {
            // refresh is in progress, we need to wait for it to finish to proceed.
            var task = accountToCheck.CurrentTask;
            if (task is not null)
            {
                await task.StartAsync(cancellationToken).ConfigureAwait(false);
                if (task.State == TaskState.AbortedByUser)
                {
                    return LaunchDecision.Abort;
                }

                state = accountToCheck.AccountState;
            }
        }

        // the C++ asks reauthenticateAccount() (dialog) for these states; the headless port
        // takes the dialog's "No" default and aborts
        if (state is AccountState.Errored or AccountState.Expired or AccountState.Disabled or AccountState.Gone)
        {
            return LaunchDecision.Abort;
        }

        _actualLaunchMode = state == AccountState.Online && WantedLaunchMode == LaunchMode.Normal
            ? LaunchMode.Normal
            : LaunchMode.Offline;
        return LaunchDecision.Continue; // All good to go
    }

    /// <summary>Port of askPlayDemo() (the C++ default button is Cancel — see the header note).</summary>
    private bool AskPlayDemo() => false;

    /// <summary>Port of askOfflineName(); returns null when the (hook) dialog was cancelled.</summary>
    private string? AskOfflineName(string playerName, LaunchMode mode)
    {
        var title = "Player name";
        var message = "";
        switch (mode)
        {
            case LaunchMode.Normal:
                throw new UnreachableException("askOfflineName is never called in Normal mode");
            case LaunchMode.Demo:
                message = "Choose your demo mode player name";
                break;
            case LaunchMode.Offline:
                if (WantedLaunchMode == LaunchMode.Normal)
                {
                    var netErr = AccountToUse?.AccountData.NetworkError ?? 0;
                    if (AuthNetUtils.IsServerError(netErr))
                    {
                        title = "Auth servers offline";
                        message = "The Minecraft authentication servers are currently unavailable, launching in offline mode.\n\n";
                    }
                    else
                    {
                        title = "No internet connection";
                        message = "You are not connected to the Internet, launching in offline mode.\n\n";
                    }
                }

                message += "Choose your offline mode player name";
                break;
            default:
                return null;
        }

        // the dialog suggestion prefers the stored last offline name, like the C++
        // (LastOfflinePlayerName — now in launcher-state.json)
        var lastName = Services?.LauncherState?.Current.LastOfflinePlayerName ?? "";
        var suggestion = lastName.Length != 0 ? lastName : playerName;

        string usedName;
        if (AskOfflineNameHandler is not null)
        {
            usedName = AskOfflineNameHandler(title, message, suggestion) ?? "";
            if (usedName.Length == 0)
            {
                return null;
            }
        }
        else
        {
            // headless default: the configured offline name, then the suggestion
            usedName = OfflineName.Length != 0 ? OfflineName : suggestion;
            if (usedName.Length == 0)
            {
                return null;
            }
        }

        Services?.LauncherState?.Update(state => state with { LastOfflinePlayerName = usedName });
        return usedName;
    }

    /// <summary>Port of login(): decide the account/mode, fill the session, launch.</summary>
    private async Task Login(MinecraftInstance instance, CancellationToken cancellationToken)
    {
        DecideAccount(instance);

        LaunchDecision decision;
        do
        {
            decision = await DecideLaunchMode(cancellationToken).ConfigureAwait(false);
        } while (decision == LaunchDecision.Undecided && !cancellationToken.IsCancellationRequested);

        if (cancellationToken.IsCancellationRequested && decision == LaunchDecision.Undecided)
        {
            // the controller was aborted while looping on the (hook driven) reauth path
            EmitAborted();
            return;
        }

        if (decision == LaunchDecision.Abort)
        {
            EmitAborted();
            return;
        }

        if (_actualLaunchMode == LaunchMode.Demo)
        {
            if (WantedLaunchMode == LaunchMode.Demo || AskPlayDemo())
            {
                var name = AskOfflineName("Player", LaunchMode.Demo);
                if (name is not null)
                {
                    _session = new AuthSession();
                    _session.MakeDemo(name, MinecraftAccount.UuidFromUsername(name).ToString("D"));
                    LaunchInstance(instance);
                    return;
                }
            }

            EmitFailed("No account selected for launch");
            return;
        }

        _session = new AuthSession
        {
            LaunchMode = _actualLaunchMode
        };
        if (AccountToUse is null)
        {
            // headless extension: an explicit offline launch without any account builds the
            // session straight from the offline name (AskOfflineName's headless default)
            var name = OfflineName.Length != 0
                ? OfflineName
                : AskOfflineName("Player", LaunchMode.Offline) ?? "";
            if (name.Length == 0)
            {
                EmitAborted();
                return;
            }

            _session.MakeOffline(name);
            LaunchInstance(instance);
            return;
        }

        AccountToUse.FillSession(_session);

        if (AccountToUse.AccountType != AccountType.Offline)
        {
            if (_actualLaunchMode == LaunchMode.Normal && !AccountToUse.HasProfile())
            {
                // Now handle setting up a profile name here... (the ProfileSetupDialog is UI;
                // the headless core cannot invent a profile name — abort like a rejected dialog)
                EmitAborted();
                return;
            }

            if (_actualLaunchMode == LaunchMode.Offline)
            {
                var name = OfflineName;
                if (name.Length == 0)
                {
                    name = AskOfflineName(_session.PlayerName, LaunchMode.Offline) ?? "";
                    if (name.Length == 0)
                    {
                        EmitAborted();
                        return;
                    }
                }

                _session.MakeOffline(name);
            }
        }

        LaunchInstance(instance);
    }

    // ---------------------------------------------------------------------- launchInstance

    /// <summary>Port of launchInstance(): build the LaunchTask, prepend the banner steps, run.</summary>
    private void LaunchInstance(MinecraftInstance instance)
    {
        _launcher = instance.CreateLaunchTask(_session, TargetToJoin, Services);
        if (_launcher is null)
        {
            EmitFailed("Couldn't instantiate a launcher.");
            return;
        }

        Log.LaunchStarted(
            instance.Id(),
            _actualLaunchMode.ToString(),
            _session?.PlayerName ?? "(none)",
            TargetToJoin?.ToString() ?? "(instance default)");

        _launcher.ReadyForLaunch += ReadyForLaunch;
        _launcher.Succeeded += OnLauncherSucceeded;
        _launcher.Failed += OnLauncherFailed;
        _launcher.RequestProgress += OnProgressRequested;

        // Prepend Online and Auth Status
        string onlineMode;
        if (_actualLaunchMode == LaunchMode.Normal)
        {
            onlineMode = "online";

            // Prepend Server Status
            string[] servers = ["login.microsoftonline.com", "session.minecraft.net", "textures.minecraft.net", "api.mojang.com"];

            _launcher.PrependStep(new PrintServers(_launcher, servers));
        }
        else
        {
            onlineMode = _actualLaunchMode == LaunchMode.Demo ? "demo" : "offline";
        }

        _launcher.PrependStep(new TextPrint(_launcher, "Launched instance in " + onlineMode + " mode\n",
            MessageLevel.Launcher));

        // Prepend Version
        {
            var versionString =
                $"{FlintConfig.Default.LauncherDisplayName} version: {FlintConfig.DefaultVersionString} ({FlintConfig.Default.BuildPlatform})";
            _launcher.PrependStep(new TextPrint(_launcher, versionString + "\n", MessageLevel.Launcher));
        }
        _ = _launcher.StartAsync();
    }

    /// <summary>Port of readyForLaunch(): the profiler plumbing is UI, so the default is an
    /// immediate proceed() (disable with AutoProceed for API gating).</summary>
    private void ReadyForLaunch()
    {
        if (AutoProceed)
        {
            _launcher?.Proceed();
        }
    }

    private void OnLauncherSucceeded(FlintTask task)
    {
        Log.LaunchFinished("succeeded");
        EmitSucceeded();
    }

    private void OnLauncherFailed(FlintTask task, string reason)
    {
        Log.LaunchFinished("failed: " + reason);
        EmitFailed(reason);
    }

    /// <summary>Port of onProgressRequested(): the progress dialog collapses into a proceed().</summary>
    private void OnProgressRequested(FlintTask task) => _launcher?.Proceed();

    // -------------------------------------------------------------------------------- abort

    public override void Abort()
    {
        if (_launcher is null)
        {
            // C++ abort(): nothing to kill before the launcher exists
            base.Abort();
            return;
        }

        if (!_launcher.CanAbort())
        {
            return;
        }

        // the C++ "Kill Minecraft?" confirm defaults to Yes; the headless port kills directly
        _launcher.Abort();
    }
}
