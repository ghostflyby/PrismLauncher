// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
// SPDX-FileCopyrightText: 2022 Sefa Eyeoglu <contact@scrumplex.net>
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
// Fetches the legacy FTB pack lists (static/modpacks.xml + static/thirdparty.xml) and the
// private pack lists (static/{code}.xml) from the CDN and parses their XML; see
// ParseAndAddPacks for the attribute set and the oldVersions repair rules.
//
// A list that does not parse is named in the thrown exception while the other list stays
// populated, so a partially broken CDN still yields the packs it did serve.

using System.Xml;
using FlintLauncher.Core.Net;

namespace FlintLauncher.Core.ModPlatform.LegacyFtb;

public sealed class LegacyFtbPackFetchTask
{
    /// <summary>BuildConfig.LEGACY_FTB_CDN_BASE_URL (per-instance; tests construct the task
    /// pointed at their stub).</summary>
    public string CdnBaseUrl { get; init; } = FlintConfig.Default.LegacyFtbCdnBaseUrl;

    /// <summary>The network stack the pack-list fetches ride.</summary>
    public required HttpClient Http { get; init; }


    public List<LegacyFtbModpack> PublicPacks { get; } = [];
    public List<LegacyFtbModpack> ThirdPartyPacks { get; } = [];

    /// <summary>privateFileDownloadFinished stand-in (private pack successfully parsed).</summary>
    public Action<LegacyFtbModpack>? PrivatePackFetched { get; set; }

    /// <summary>Fetches and parses both shared pack lists. A list that does not parse is
    /// named in the thrown exception while the other list stays populated.</summary>
    public async Task FetchAsync(CancellationToken cancellationToken)
    {
        PublicPacks.Clear();
        ThirdPartyPacks.Clear();

        var publicPacksUrl = CdnBaseUrl + "static/modpacks.xml";
        var thirdPartyUrl = CdnBaseUrl + "static/thirdparty.xml";

        string publicXml;
        string thirdPartyXml;
        try
        {
            publicXml = await FlintHttp.GetStringAsync(Http,
                new HttpRequestMessage(HttpMethod.Get, publicPacksUrl), cancellationToken);
            thirdPartyXml = await FlintHttp.GetStringAsync(Http,
                new HttpRequestMessage(HttpMethod.Get, thirdPartyUrl), cancellationToken);
        }
        catch (FlintHttpException e)
        {
            throw new InvalidOperationException(e.Reason, e);
        }

        FileDownloadFinished(publicXml, thirdPartyXml);
    }

    /// <summary>Fetches static/{code}.xml per pack code; each parsed pack carries its
    /// packCode. A code that fails to fetch or parse is skipped silently, so the caller learns
    /// about it only by its pack's absence.</summary>
    public async Task FetchPrivateAsync(IEnumerable<string> toFetch, CancellationToken cancellationToken = default)
    {
        var privatePackBaseUrl = CdnBaseUrl + "static/%1.xml";

        foreach (var packCode in toFetch)
        {
            var url = privatePackBaseUrl.Replace("%1", packCode);

            string body;
            try
            {
                body = await FlintHttp.GetStringAsync(Http,
                    new HttpRequestMessage(HttpMethod.Get, url), cancellationToken);
            }
            catch (FlintHttpException)
            {
                continue;
            }

            var packs = new List<LegacyFtbModpack>();
            if (!ParseAndAddPacks(body, PackType.Private, packs))
            {
                // an unparseable private pack body contributes no packs
                continue;
            }

            foreach (var currentPack in packs)
            {
                currentPack.PackCode = packCode;
                PrivatePackFetched?.Invoke(currentPack);
            }
        }
    }

    private void FileDownloadFinished(string publicResponse, string thirdPartyResponse)
    {
        var failedLists = new List<string>();

        if (!ParseAndAddPacks(publicResponse, PackType.Public, PublicPacks))
        {
            failedLists.Add("Public Packs");
        }

        if (!ParseAndAddPacks(thirdPartyResponse, PackType.ThirdParty, ThirdPartyPacks))
        {
            failedLists.Add("Third Party Packs");
        }

        if (failedLists.Count > 0)
        {
            throw new InvalidOperationException(
                "Failed to download some pack lists: " + string.Join("\n- ", failedLists));
        }
    }

    /// <summary>Parses every &lt;modpack&gt; element of the XML document into the list.
    /// Returns false for unparseable XML.</summary>
    public static bool ParseAndAddPacks(string data, PackType packType, List<LegacyFtbModpack> list)
    {
        var doc = new XmlDocument();
        try
        {
            doc.LoadXml(data);
        }
        catch (XmlException)
        {
            return false;
        }

        var nodes = doc.GetElementsByTagName("modpack");
        foreach (XmlNode node in nodes)
        {
            var element = (XmlElement)node;

            var modpack = new LegacyFtbModpack
            {
                Name = element.GetAttribute("name"),
                CurrentVersion = element.GetAttribute("version"),
                McVersion = element.GetAttribute("mcVersion"),
                Description = element.GetAttribute("description"),
                Mods = element.GetAttribute("mods"),
                Logo = element.GetAttribute("logo"),
                Broken = false,
                Bugged = false,
            };
            modpack.OldVersions.AddRange(element.GetAttribute("oldVersions").Split(';'));

            // remove empty if the xml is bugged
            for (var i = modpack.OldVersions.Count - 1; i >= 0; i--)
            {
                var curr = modpack.OldVersions[i];
                if (string.IsNullOrEmpty(curr))
                {
                    modpack.OldVersions.RemoveAt(i);
                    modpack.Bugged = true;
                }
            }

            if (modpack.OldVersions.Count < 1)
            {
                if (modpack.CurrentVersion.Length != 0)
                {
                    modpack.OldVersions.Add(modpack.CurrentVersion);
                }
                else
                {
                    modpack.Broken = true;
                }
            }

            modpack.Author = element.GetAttribute("author");

            modpack.Dir = element.GetAttribute("dir");
            modpack.File = element.GetAttribute("url");

            modpack.Type = packType;

            list.Add(modpack);
        }

        return true;
    }
}
