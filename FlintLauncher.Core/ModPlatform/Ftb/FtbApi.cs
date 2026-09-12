// SPDX-License-Identifier: GPL-3.0-only
// The FTB App API surface used by the platform layer: the modpacks.ch index URL
// (/modpack/all -> {"packs":[id,...]}), the per-pack description (/modpack/{id}) and the
// per-version payload (/modpack/{id}/{versionId}). BuildConfig.FTB_API_BASE_URL becomes a
// settable hook (tests restore it — see FtbTests for the [Collection] pattern).
using System.Text.Json;
using System.Text.Json.Nodes;
using FlintLauncher.Core.Json;
using FlintLauncher.Core.Net;

namespace FlintLauncher.Core.ModPlatform.Ftb;

public static class FtbApi
{
    /// <summary>BuildConfig.FTB_API_BASE_URL (settable; tests restore it).</summary>
    public static string BaseUrl { get; set; } = FlintConfig.Default.FtbApiBaseUrl;

    // ------------------------------------------------------------------ URL builders

    /// <summary>FtbListModel::request's /modpack/all URL.</summary>
    public static string ModpacksUrl() => BaseUrl + "/modpack/all";

    /// <summary>FtbListModel::requestPack's /modpack/{id} URL.</summary>
    public static string ModpackUrl(int packId) => $"{BaseUrl}/modpack/{packId}";

    /// <summary>PackInstallTask::executeTask's /modpack/{id}/{versionId} URL.</summary>
    public static string VersionUrl(int packId, int versionId) => $"{BaseUrl}/modpack/{packId}/{versionId}";

    // ---------------------------------------------------------------------- requests

    /// <summary>makeByteArray(/modpack/all).</summary>
    public static (NetJob Job, NetRequest Request) FetchModpacks()
    {
        var netJob = new NetJob("Ftb::Request");
        var request = NetRequest.MakeByteArray(new Uri(ModpacksUrl()));
        netJob.AddNetRequest(request);
        return (netJob, request);
    }

    /// <summary>makeByteArray(/modpack/{id}).</summary>
    public static (NetJob Job, NetRequest Request) FetchModpack(int packId)
    {
        var netJob = new NetJob("Ftb::Search");
        var request = NetRequest.MakeByteArray(new Uri(ModpackUrl(packId)));
        netJob.AddNetRequest(request);
        return (netJob, request);
    }

    /// <summary>makeByteArray(/modpack/{id}/{versionId}).</summary>
    public static (NetJob Job, NetRequest Request) FetchVersion(int packId, int versionId)
    {
        var netJob = new NetJob("FTB::VersionFetch");
        var request = NetRequest.MakeByteArray(new Uri(VersionUrl(packId, versionId)));
        netJob.AddNetRequest(request);
        return (netJob, request);
    }

    // ----------------------------------------------------------------------- parsers

    /// <summary>Parses the /modpack/all index (FtbListModel::requestFinished):
    /// {"packs":[id,...]} -> the list of pack ids.</summary>
    public static List<int> LoadModpackIndex(string response)
    {
        using var doc = JsonUtils.RequireDocument(response, "JSON response from FTB");
        var packs = new List<int>();
        if (doc.RootElement.TryGetProperty("packs", out var packsRaw)
            && packsRaw.ValueKind == JsonValueKind.Array)
        {
            foreach (var pack in packsRaw.EnumerateArray())
            {
                // QJsonValue::toInt() coercion: doubles truncate, junk becomes 0
                packs.Add(pack.ValueKind == JsonValueKind.Number
                    ? (int)pack.GetDouble()
                    : 0);
            }
        }
        return packs;
    }

    /// <summary>Parses a /modpack/{id} response (FtbListModel::packRequestFinished).</summary>
    public static FtbModpack LoadModpack(string response)
    {
        using var doc = JsonUtils.RequireDocument(response, "JSON response from FTB");
        var pack = new FtbModpack();
        FtbPackManifest.LoadModpack(pack, RequireObject(doc.RootElement));
        return pack;
    }

    /// <summary>Parses a /modpack/{id}/{versionId} response (PackInstallTask).</summary>
    public static FtbVersion LoadVersion(string response)
    {
        using var doc = JsonUtils.RequireDocument(response, "JSON response from FTB");
        var version = new FtbVersion();
        FtbPackManifest.LoadVersion(version, RequireObject(doc.RootElement));
        return version;
    }

    private static JsonObject RequireObject(JsonElement element)
    {
        return JsonNode.Parse(element.GetRawText()) as JsonObject
            ?? throw new JsonParseException("Document is not an object");
    }
}
