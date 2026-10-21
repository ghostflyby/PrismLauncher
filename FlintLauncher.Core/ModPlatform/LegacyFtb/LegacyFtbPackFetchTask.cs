// SPDX-License-Identifier: GPL-3.0-only
// Port of modplatform/legacy_ftb/PackFetchTask.{h,cpp}: fetch the legacy FTB pack lists
// (static/modpacks.xml + static/thirdparty.xml) and the private pack lists
// (static/{code}.xml) from the CDN and parse their XML (see ParseAndAddPacks for the exact
// attribute set and the oldVersions repair rules).
//
// Deviations (headless port): the finished/failed/privateFileDownload* signals become the
// result properties + the PrivatePackFetched callback; a partially failed fetch keeps the
// successfully parsed lists and fails with the C++ "Failed to download some pack lists"
// message (the C++ emits failed while still handing out the parsed lists through the same
// signal arguments).

using System.Xml;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Tasks;

namespace FlintLauncher.Core.ModPlatform.LegacyFtb;

public sealed class LegacyFtbPackFetchTask : FlintTask
{
    /// <summary>BuildConfig.LEGACY_FTB_CDN_BASE_URL (per-instance; tests construct the task
    /// pointed at their stub).</summary>
    public string CdnBaseUrl { get; init; } = FlintConfig.Default.LegacyFtbCdnBaseUrl;

    public List<LegacyFtbModpack> PublicPacks { get; } = [];
    public List<LegacyFtbModpack> ThirdPartyPacks { get; } = [];

    /// <summary>privateFileDownloadFinished stand-in (private pack successfully parsed).</summary>
    public Action<LegacyFtbModpack>? PrivatePackFetched { get; set; }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        PublicPacks.Clear();
        ThirdPartyPacks.Clear();

        var publicPacksUrl = CdnBaseUrl + "static/modpacks.xml";
        var thirdPartyUrl = CdnBaseUrl + "static/thirdparty.xml";

        var job = new NetJob("LegacyFTB::ModpackFetch");
        var publicRequest = NetRequest.MakeByteArray(new Uri(publicPacksUrl));
        var thirdPartyRequest = NetRequest.MakeByteArray(new Uri(thirdPartyUrl));
        job.AddNetRequest(publicRequest);
        job.AddNetRequest(thirdPartyRequest);

        await job.StartAsync(cancellationToken);
        if (!job.WasSuccessful)
        {
            EmitFailed(job.FailReason);
            return;
        }

        await FileDownloadFinished(publicRequest.BodyAsString, thirdPartyRequest.BodyAsString);
    }

    /// <summary>Port of PackFetchTask::fetchPrivate: fetch static/{code}.xml per pack code;
    /// each parsed pack carries its packCode. Failures are skipped per code (the C++ reported
    /// them through the privateFileDownloadFailed signal, whose port has no subscriber). The
    /// task result only reflects the shared network job.</summary>
    public async Task FetchPrivateAsync(IEnumerable<string> toFetch, CancellationToken cancellationToken = default)
    {
        var privatePackBaseUrl = CdnBaseUrl + "static/%1.xml";

        foreach (var packCode in toFetch)
        {
            var url = privatePackBaseUrl.Replace("%1", packCode);
            var job = new NetJob("Fetching private pack");
            var request = NetRequest.MakeByteArray(new Uri(url));
            job.AddNetRequest(request);

            await job.StartAsync(cancellationToken);
            if (!job.WasSuccessful)
            {
                continue;
            }

            var packs = new List<LegacyFtbModpack>();
            if (!ParseAndAddPacks(request.BodyAsString, PackType.Private, packs))
            {
                // (the C++ signals nothing for an unparseable private pack body — the
                //  finished signal simply never fires for it)
                continue;
            }

            foreach (var currentPack in packs)
            {
                currentPack.PackCode = packCode;
                PrivatePackFetched?.Invoke(currentPack);
            }
        }
    }

    private async Task FileDownloadFinished(string publicResponse, string thirdPartyResponse)
    {
        await Task.CompletedTask;

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
            EmitFailed("Failed to download some pack lists: " + string.Join("\n- ", failedLists));
            return;
        }

        EmitSucceeded();
    }

    /// <summary>Port of PackFetchTask::parseAndAddPacks: parse every &lt;modpack&gt; element
    /// of the XML document into the list. Returns false for unparseable XML.</summary>
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
