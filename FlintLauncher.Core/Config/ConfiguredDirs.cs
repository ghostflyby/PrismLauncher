// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

using Microsoft.Extensions.Options;
using TruePath;

namespace FlintLauncher.Core.Config;

/// <summary>The dir-kind settings resolved against the roots. JavaDir follows store
/// semantics (absolute wins as-is, relative joins the store root, empty falls back to the
/// store/java anchor — the C++ "relative to the data dir" convention re-anchored by the store
/// remount); IconsDir/SkinsDir follow the C++ "relative to the data dir" convention (absolute
/// wins as-is, relative joins the data root, empty falls back to the C++ default name). Every
/// member reads the live snapshot, so a settings write is visible on the next access.</summary>
public sealed class ConfiguredDirs(FlintPaths paths, IOptionsMonitor<FlintConfigFile> config)
{
    /// <summary>The launcher-managed java root: consumed by the java scan, the
    /// JavaInstallList managed-versions probe and the launch pipeline's AutoInstallJava.</summary>
    public AbsolutePath JavaRoot =>
        string.IsNullOrWhiteSpace(config.CurrentValue.Dirs.JavaDir)
            ? paths.JavaRoot
            : Path.IsPathRooted(config.CurrentValue.Dirs.JavaDir)
                ? new AbsolutePath(config.CurrentValue.Dirs.JavaDir)
                : paths.StoreRoot / config.CurrentValue.Dirs.JavaDir;

    public AbsolutePath IconsDir => DataRelative(config.CurrentValue.Dirs.IconsDir, "icons");

    public AbsolutePath SkinsDir => DataRelative(config.CurrentValue.Dirs.SkinsDir, "skins");

    private AbsolutePath DataRelative(string? configured, string fallbackName) =>
        string.IsNullOrWhiteSpace(configured)
            ? paths.DataDir / fallbackName
            : Path.IsPathRooted(configured)
                ? new AbsolutePath(configured)
                : paths.DataDir / configured;
}
