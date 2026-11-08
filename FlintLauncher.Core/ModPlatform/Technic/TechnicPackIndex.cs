// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2021 Jamie Mansfield <jmansfield@cadixdev.org>
// SPDX-FileCopyrightText: 2021-2022 Jamie Mansfield <jmansfield@cadixdev.org>
//
// This file incorporates work covered by the following copyright and
// permission notice:
//
//     Copyright 2020-2021 MultiMC Contributors
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
// The mapping half of the Technic page/model
// (ui/pages/modplatform/technic/TechnicModel.cpp + TechnicPage.cpp + TechnicData.h): the
// search answers into the page's Modpack entries (the "vanilla" slug exclusion, the
// icon/"secondary" logo handling) and the pack metadata with the url-vs-solder detection.
// The requests and the document decode live with the Technic client
// (FlintLauncher.ExternalApi.Technic).

using System.Text.Json;
using FlintLauncher.ExternalApi.Technic;

namespace FlintLauncher.Core.ModPlatform.Technic;

/// <summary>Port of Technic::Modpack (TechnicData.h).</summary>
public sealed class TechnicModpack
{
    public string Slug { get; init; } = "";

    public string Name { get; init; } = "";
    public string LogoUrl { get; set; } = "";
    public string LogoName { get; set; } = "";

    public bool Broken { get; set; } = true;

    public string Url { get; set; } = "";
    public bool IsSolder { get; set; }
    public string MinecraftVersion { get; set; } = "";

    public bool MetadataLoaded { get; set; }
    public string WebsiteUrl { get; set; } = "";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    public string CurrentVersion { get; set; } = "";

    public bool VersionsLoaded { get; set; }
    public string Recommended { get; set; } = "";
    public List<string> Versions { get; } = [];
}

/// <summary>The page/model mapping of the decoded Technic answers into
/// <see cref="TechnicModpack"/> entries.</summary>
public static class TechnicPackIndex
{
    private static string RequireMember(string? value, string key) =>
        value ?? throw new JsonException("'" + key + "' is not a string");

    private static List<T> RequireMember<T>(List<T>? value, string key) where T : class =>
        value ?? throw new JsonException("'" + key + "' is not an array");

    /// <summary>Port of ListModel::searchRequestFinished. Returns the parsed packs; an empty
    /// list mirrors the C++ parse-failure / "error" payload paths (nothing is appended).</summary>
    public static List<TechnicModpack> LoadSearchResults(TechnicSearchResponse response)
    {
        var newList = new List<TechnicModpack>();
        try
        {
            switch (response.Mode)
            {
                case TechnicSearchMode.List:
                {
                    var root = response.List;
                    foreach (var raw in RequireMember(root?.Modpacks, "modpacks"))
                    {
                        var pack = new TechnicModpack
                        {
                            Name = RequireMember(raw.Name, "name"),
                            Slug = RequireMember(raw.Slug, "slug"),
                        };
                        if (pack.Slug == "vanilla")
                        {
                            continue;
                        }

                        var rawUrl = raw.IconUrl;
                        if (rawUrl is null or "null")
                        {
                            pack.LogoUrl = "null";
                            pack.LogoName = "null";
                        }
                        else
                        {
                            pack.LogoUrl = rawUrl;
                            pack.LogoName = pack.Slug + "." + UrlFileNameSuffix(rawUrl);
                        }

                        pack.Broken = false;
                        newList.Add(pack);
                    }

                    break;
                }
                case TechnicSearchMode.Single:
                {
                    var root = response.Single;
                    if (root?.Error is not null)
                    {
                        // Invalid API url
                        break;
                    }

                    var pack = new TechnicModpack
                    {
                        Name = RequireMember(root?.DisplayName, "displayName"),
                        Slug = RequireMember(root?.Name, "name"),
                    };

                    if (root?.Icon?.Url is { } iconUrl)
                    {
                        pack.LogoUrl = iconUrl;
                        pack.LogoName = pack.Slug + "." + UrlFileNameSuffix(iconUrl);
                    }
                    else
                    {
                        pack.LogoUrl = "null";
                        pack.LogoName = "null";
                    }

                    pack.Broken = false;
                    newList.Add(pack);
                    break;
                }
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return newList;
    }

    /// <summary>
    /// Port of TechnicPage::onSlectionChanged's metadata step (the metadataLoaded JSON):
    /// fills url/isSolder (a string "url" wins over a string "solder"; neither fails the
    /// load), minecraft, platformUrl, user, description and version. Returns false when the
    /// payload is missing (malformed) or has neither a string url nor a string solder key
    /// (upstream bails out with a warning).
    /// </summary>
    public static bool LoadPackMetadata(TechnicMetadataJson? obj, TechnicModpack current)
    {
        if (obj?.Url is { } url)
        {
            current.Url = url;
        }
        else if (obj?.Solder is { } solderUrl)
        {
            current.Url = solderUrl;
            current.IsSolder = true;
        }
        else
        {
            return false;
        }

        current.MinecraftVersion = obj.Minecraft ?? "";
        current.WebsiteUrl = obj.PlatformUrl ?? "";
        current.Author = obj.User ?? "";
        current.Description = obj.Description ?? "";
        current.CurrentVersion = obj.Version ?? "";
        current.MetadataLoaded = true;
        return true;
    }

    /// <summary>QFileInfo(QUrl(url).fileName()).suffix() equivalent: the extension of the
    /// last path segment (without the dot).</summary>
    private static string UrlFileNameSuffix(string url)
    {
        var fileName = url;
        var slash = fileName.LastIndexOf('/');
        if (slash >= 0)
        {
            fileName = fileName[(slash + 1)..];
        }

        // strip a query string, if any
        var query = fileName.IndexOf('?');
        if (query >= 0)
        {
            fileName = fileName[..query];
        }

        var dot = fileName.LastIndexOf('.');
        return dot >= 0 ? fileName[(dot + 1)..] : "";
    }
}
