// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

using TruePath;

namespace FlintLauncher.Core.Launch;

/// <summary>The C++ GUI touchpoints of the launch pipeline, as injected hooks instead of
/// static fields. All nullable: unset means the headless default (skip).</summary>
public sealed record LaunchUiHooks
{
    /// <summary>CloseAfterLaunch mode: the game logged "Setting user ..." — close the launcher
    /// windows (LauncherPartLaunch).</summary>
    public Action? CloseAfterLaunch { get; init; }

    /// <summary>CloseAfterLaunch mode: the game exited — show the main window again
    /// (LauncherPartLaunch).</summary>
    public Action? ShowMainWindow { get; init; }

    /// <summary>Low memory pressure warning text — return true to proceed with the launch,
    /// false to abort (EnsureAvailableMemory; the C++ shows a confirm dialog).</summary>
    public Func<string, bool>? ConfirmLaunch { get; init; }

    /// <summary>The game stopped and the C++ requested quitting the application
    /// (QuitAfterGameStop).</summary>
    public Action? QuitRequested { get; init; }

    /// <summary>Copy the instance icon into the game root (icon path, target path)
    /// (MinecraftInstance; C++ APPLICATION->icons()->saveIcon).</summary>
    public Action<string, AbsolutePath>? SaveIcon { get; init; }

    /// <summary>Primary screen available size for maximized launch scripts; unset falls back
    /// to the configured window size (MinecraftInstance).</summary>
    public Func<(int Width, int Height)>? PrimaryScreenSize { get; init; }
}
