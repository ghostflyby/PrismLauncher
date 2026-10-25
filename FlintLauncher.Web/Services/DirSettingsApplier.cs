// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

using FlintLauncher.Core.Config;
using FlintLauncher.Core.Icons;
using FlintLauncher.Core.Skins;

namespace FlintLauncher.Web.Services;

/// <summary>Re-points the dir-kind models (icons/skins) after a Dirs settings write: the
/// new roots are created on demand and the models rebuild their indexes and re-point their
/// watches.</summary>
public sealed class DirSettingsApplier(ConfiguredDirs dirs, IconPool icons, SkinsLibrary skins)
{
    public void Apply()
    {
        icons.SetIconsDir(dirs.IconsDir);
        skins.SetSkinsDir(dirs.SkinsDir);
    }
}
