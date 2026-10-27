// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Port of the Modrinth version-spelling helpers (ResourceAPI::mapMCVersionToModrinth /
// mapMCVersionFromModrinth): the launcher spells a pre-release "1.20.2 Pre-Release 1", the
// Modrinth wire "1.20.2-pre1".

using FlintLauncher.Core.Versioning;

namespace FlintLauncher.Core.ModPlatform.Modrinth;

public static class ModrinthVersionFormat
{
    /// <summary>The launcher spelling of a version as the Modrinth wire expects it.</summary>
    public static string ToWire(McVersion v)
    {
        const string preString = " Pre-Release ";
        var verStr = v.ToString();

        if (verStr.Contains(preString))
        {
            verStr = verStr.Replace(preString, "-pre");
        }

        verStr = verStr.Replace(" ", "-");
        return verStr;
    }

    /// <summary>The reverse of <see cref="ToWire"/>: the indexed wire data comes back in
    /// the launcher's version spelling.</summary>
    public static string FromWire(string v)
    {
        const string preString = " Pre-Release ";
        var pre = v.Contains("-pre", StringComparison.Ordinal);
        if (pre)
        {
            v = v.Replace("-pre", preString);
        }

        v = v.Replace("-", " ");
        if (pre)
        {
            v = v.Replace(" Pre Release ", preString);
        }

        return v;
    }
}
