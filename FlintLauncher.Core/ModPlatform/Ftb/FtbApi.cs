// SPDX-License-Identifier: GPL-3.0-only
// The FTB App API surface used by the platform layer: the modpacks.ch index URL
// (/modpack/all -> {"packs":[id,...]}), the per-pack description (/modpack/{id}) and the
// per-version payload (/modpack/{id}/{versionId}).
//
// BuildConfig.FTB_API_BASE_URL became injected instance state: BaseUrl is per-instance
// (tests construct their own client pointed at a stub; the install endpoint derives one
// for its serverUrl override — see FtbEndpoints); the client rides the injected NetStack
// (LauncherRuntime.Ftb — see the Modrinth/Flame de-singletoning for the same story). The
// parsers are pure helpers and stay static; reads go through the JsonNode model.

using System.Text.Json;
using System.Text.Json.Nodes;
using FlintLauncher.Core.Json;
using FlintLauncher.Core.Net;

namespace FlintLauncher.Core.ModPlatform.Ftb;

public sealed class FtbApi
{
    /// <summary>BuildConfig.FTB_API_BASE_URL (per-instance; tests construct a client
    /// pointed at their stub).</summary>
    public string BaseUrl { get; set; } = FlintConfig.Default.FtbApiBaseUrl;

    /// <summary>The network stack this client's requests go through.</summary>
    public NetStack Net { get; }

    public FtbApi(NetStack? net = null)
    {
        Net = net ?? NetStack.Default;
    }

    // ------------------------------------------------------------------ URL builders

    /// <summary>FtbListModel::request's /modpack/all URL.</summary>
    public string ModpacksUrl() => BaseUrl + "/modpack/all";

    /// <summary>FtbListModel::requestPack's /modpack/{id} URL.</summary>
    public string ModpackUrl(int packId) => $"{BaseUrl}/modpack/{packId}";

    /// <summary>PackInstallTask::executeTask's /modpack/{id}/{versionId} URL.</summary>
    public string VersionUrl(int packId, int versionId) => $"{BaseUrl}/modpack/{packId}/{versionId}";

    // ---------------------------------------------------------------------- requests

    /// <summary>makeByteArray(/modpack/all).</summary>
    public (NetJob Job, NetRequest Request) FetchModpacks()
    {
        var netJob = new NetJob("Ftb::Request");
        var request = NetRequest.MakeByteArray(new Uri(ModpacksUrl()), stack: Net);
        netJob.AddNetRequest(request);
        return (netJob, request);
    }

    /// <summary>makeByteArray(/modpack/{id}).</summary>
    public (NetJob Job, NetRequest Request) FetchModpack(int packId)
    {
        var netJob = new NetJob("Ftb::Search");
        var request = NetRequest.MakeByteArray(new Uri(ModpackUrl(packId)), stack: Net);
        netJob.AddNetRequest(request);
        return (netJob, request);
    }

    /// <summary>makeByteArray(/modpack/{id}/{versionId}).</summary>
    public (NetJob Job, NetRequest Request) FetchVersion(int packId, int versionId)
    {
        var netJob = new NetJob("FTB::VersionFetch");
        var request = NetRequest.MakeByteArray(new Uri(VersionUrl(packId, versionId)), stack: Net);
        netJob.AddNetRequest(request);
        return (netJob, request);
    }

    // ----------------------------------------------------------------------- parsers

    /// <summary>Node-side Json::requireDocument(QByteArray, what): JsonNode.Parse with the
    /// parse-error wrapping (the null parse result of a "null" document throws like any
    /// other parse failure).</summary>
    private static JsonNode RequireDocument(string json, string what)
    {
        try
        {
            return JsonNode.Parse(json)
                   ?? throw new JsonParseException(what + ": Error parsing JSON: document is null");
        }
        catch (JsonException e)
        {
            throw new JsonParseException(what + ": Error parsing JSON: " + e.Message);
        }
    }

    /// <summary>Parses the /modpack/all index (FtbListModel::requestFinished):
    /// {"packs":[id,...]} -> the list of pack ids.</summary>
    public static List<int> LoadModpackIndex(string response)
    {
        var root = RequireDocument(response, "JSON response from FTB");
        var packs = new List<int>();
        if (root is JsonObject map && map["packs"] is JsonArray packsRaw)
        {
            foreach (var pack in packsRaw)
            {
                // QJsonValue::toInt() coercion: doubles truncate, junk becomes 0
                packs.Add(JsonUtils.EnsureInteger(pack));
            }
        }

        return packs;
    }

    /// <summary>Parses a /modpack/{id} response (FtbListModel::packRequestFinished).</summary>
    public static FtbModpack LoadModpack(string response)
    {
        var root = RequireDocument(response, "JSON response from FTB");
        if (root is not JsonObject)
        {
            throw new JsonParseException("Document is not an object");
        }

        var pack = new FtbModpack();
        FtbPackManifest.LoadModpack(pack, root);
        return pack;
    }

    /// <summary>Parses a /modpack/{id}/{versionId} response (PackInstallTask).</summary>
    public static FtbVersion LoadVersion(string response)
    {
        var root = RequireDocument(response, "JSON response from FTB");
        if (root is not JsonObject)
        {
            throw new JsonParseException("Document is not an object");
        }

        var version = new FtbVersion();
        FtbPackManifest.LoadVersion(version, root);
        return version;
    }
}
