// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022 Sefa Eyeoglu <contact@scrumplex.net>
// SPDX-FileCopyrightText: 2023-2025 Trial97 <alexandru.tripon97@gmail.com>
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
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
// Port of the data/abstract part of modplatform/ResourceAPI.{h,cpp}: the common interface
// for interacting with mod-platform APIs (search args/sorting models and the shared
// version-string mapping helpers). The Qt slot/callback machinery (searchProjects and the
// Callback<T> plumbing) is not ported — callers drive the returned NetRequest directly.

using System.Text;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.Versioning;

namespace FlintLauncher.Core.ModPlatform;

/// <summary>Port of ResourceAPI::SortingMethod.</summary>
public sealed record SortingMethod
{
    /// <summary>The index of the sorting method (used by Flame in the API request).</summary>
    public required uint Index { get; init; }

    /// <summary>The real name of the sorting, as used in the respective API specification
    /// (the display-only readable name is not ported).</summary>
    public required string Name { get; init; }
}

/// <summary>Port of ResourceAPI::SearchArgs.</summary>
public sealed class SearchArgs
{
    public ResourceType Type { get; init; } = ResourceType.Unknown;
    public int Offset { get; init; }

    public string? Search { get; init; }
    public SortingMethod? Sorting { get; init; }
    public ModLoaderType? Loaders { get; init; }
    public List<McVersion>? Versions { get; init; }
    public List<string>? CategoryIds { get; init; }
    public bool OpenSource { get; init; }
}

/// <summary>Port of ResourceAPI::VersionSearchArgs.</summary>
public sealed class VersionSearchArgs
{
    public required IndexedPack Pack { get; init; }

    public List<McVersion>? McVersions { get; init; }
    public ModLoaderType? Loaders { get; init; }
    public ResourceType ResourceType { get; set; } = ResourceType.Unknown;
}

/// <summary>Port of ResourceAPI::ProjectInfoArgs.</summary>
public sealed class ProjectInfoArgs
{
    public required IndexedPack Pack { get; init; }
}

/// <summary>Port of ResourceAPI::DependencySearchArgs.</summary>
public sealed class DependencySearchArgs
{
    public required Dependency Dependency { get; init; }
    public required McVersion McVersion { get; init; }
    public ModLoaderType Loader { get; init; } = ModLoaderType.None;
    public bool IncludeChangelog { get; init; }
}

/// <summary>
/// Port of the ResourceAPI abstract class (URL builders + data loading hooks). Concrete
/// platforms (ModrinthApi) re-implement the URL construction and the JSON loading.
/// </summary>
public abstract class ResourceApi
{
    /// <summary>Gets a list of available sorting methods for this API.</summary>
    public abstract List<SortingMethod> GetSortingMethods();

    public abstract string? GetSearchUrl(SearchArgs args);

    public abstract string? GetInfoUrl(string id);

    public abstract string? GetVersionsUrl(VersionSearchArgs args);

    public abstract string GetDependencyUrl(DependencySearchArgs args);

    // ------------------------------------------------------------------ helpers

    /// <summary>Port of ResourceAPI::mapMCVersionToModrinth.</summary>
    public static string MapMcVersionToModrinth(McVersion v)
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

    /// <summary>Port of ResourceAPI::getGameVersionsString.</summary>
    public static string GetGameVersionsString(IReadOnlyList<McVersion> mcVersions)
    {
        var s = new StringBuilder();
        foreach (var ver in mcVersions)
        {
            s.Append('"').Append(MapMcVersionToModrinth(ver)).Append("\",");
        }

        if (s.Length > 0)
        {
            s.Length -= 1; // remove last comma
        }

        return s.ToString();
    }
}
