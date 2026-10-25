// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
//
// Port of launcher/InstanceCopyPrefs.{h,cpp}: the checkbox set of the "copy instance" flow
// and the exclusion-filter builder fed to the copy task.
//
// Deviation: the C++ getter/setter pairs (isCopySavesEnabled()/enableCopySaves() etc.)
// collapse into plain auto-properties with the same defaults. The C++ enableAllActions()
// mentioned in older revisions does not exist here; AllTrue() is the aggregate used
// upstream. Note that the link-related switches do not gate AllTrue() (they are transport
// modes, not content choices) — exactly like the C++.

namespace FlintLauncher.Core.Instances;

public sealed class InstanceCopyPrefs
{
    public bool CopySaves { get; init; } = true;
    public bool KeepPlaytime { get; init; } = true;
    public bool CopyGameOptions { get; init; } = true;
    public bool CopyResourcePacks { get; init; } = true;
    public bool CopyShaderPacks { get; init; } = true;
    public bool CopyServers { get; init; } = true;
    public bool CopyMods { get; init; } = true;
    public bool CopyScreenshots { get; init; } = true;
    public bool UseSymLinks { get; init; }
    public bool LinkRecursively { get; init; }
    public bool UseHardLinks { get; init; }
    public bool DontLinkSaves { get; init; }
    public bool UseClone { get; init; }

    /// <summary>Port of InstanceCopyPrefs::allTrue.</summary>
    public bool AllTrue()
    {
        return CopySaves && KeepPlaytime && CopyGameOptions && CopyResourcePacks && CopyShaderPacks && CopyServers &&
               CopyMods && CopyScreenshots;
    }

    /// <summary>Port of getSelectedFiltersAsRegex().</summary>
    public string GetSelectedFiltersAsRegex() => GetSelectedFiltersAsRegex([]);

    /// <summary>
    /// Port of getSelectedFiltersAsRegex(additionalFilters): a single regex string of the
    /// selected folders/files to filter out, e.g. ".minecraft/saves|.minecraft/server.dat".
    /// </summary>
    public string GetSelectedFiltersAsRegex(IReadOnlyList<string> additionalFilters)
    {
        var filters = new List<string>();

        if (!CopySaves)
        {
            filters.Add("saves");
        }

        if (!CopyGameOptions)
        {
            filters.Add("options.txt");
        }

        if (!CopyResourcePacks)
        {
            filters.Add("resourcepacks");
            filters.Add("texturepacks");
        }

        if (!CopyShaderPacks)
        {
            filters.Add("shaderpacks");
        }

        if (!CopyServers)
        {
            filters.Add("servers.dat");
            filters.Add("servers.dat_old");
            filters.Add("server-resource-packs");
        }

        if (!CopyMods)
        {
            filters.Add("coremods");
            filters.Add("mods");
            filters.Add("config");
        }

        if (!CopyScreenshots)
        {
            filters.Add("screenshots");
        }

        filters.AddRange(additionalFilters);

        // If we have any filters to add, join them as a single regex string to return:
        if (filters.Count != 0)
        {
            const string mcRoot = "[.]?minecraft/";
            // Ensure the first filter starts with the root, then join the others with an OR
            // regex before the root (ex: ".minecraft/saves|.minecraft/mods"):
            return mcRoot + string.Join("|" + mcRoot, filters);
        }

        return "";
    }
}
