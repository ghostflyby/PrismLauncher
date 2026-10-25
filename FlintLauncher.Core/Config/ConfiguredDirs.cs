// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

using TruePath;

namespace FlintLauncher.Core.Config;

/// <summary>The dir-kind settings resolved against the roots. JavaDir follows store
/// semantics (absolute wins as-is, relative joins the store root, empty falls back to the
/// store/java anchor — the C++ "relative to the data dir" convention re-anchored by the store
/// remount); IconsDir/SkinsDir follow the C++ "relative to the data dir" convention (absolute
/// wins as-is, relative joins the data root, empty falls back to the C++ default name). Every
/// member reads the live store, so a settings write is visible on the next access.</summary>
public sealed class ConfiguredDirs(FlintPaths paths, FlintConfigStore config)
{
    /// <summary>The launcher-managed java root: consumed by the java scan, the
    /// JavaInstallList managed-versions probe and the launch pipeline's AutoInstallJava.</summary>
    public AbsolutePath JavaRoot =>
        string.IsNullOrWhiteSpace(config.Current.Dirs.JavaDir)
            ? paths.JavaRoot
            : Path.IsPathRooted(config.Current.Dirs.JavaDir)
                ? new AbsolutePath(config.Current.Dirs.JavaDir)
                : paths.StoreRoot / config.Current.Dirs.JavaDir;

    public AbsolutePath IconsDir => DataRelative(config.Current.Dirs.IconsDir, "icons");

    public AbsolutePath SkinsDir => DataRelative(config.Current.Dirs.SkinsDir, "skins");

    private AbsolutePath DataRelative(string? configured, string fallbackName) =>
        string.IsNullOrWhiteSpace(configured)
            ? paths.DataDir / fallbackName
            : Path.IsPathRooted(configured)
                ? new AbsolutePath(configured)
                : paths.DataDir / configured;
}
