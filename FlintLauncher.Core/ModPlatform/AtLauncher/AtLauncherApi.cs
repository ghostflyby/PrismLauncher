// SPDX-License-Identifier: GPL-3.0-only
// The ATLauncher API surface used by the platform layer: the packsnew.json index URL
// (AtlListModel), the per-pack/version Configs.json + Configs.zip and mod/library download
// URLs (ATLPackInstallTask), the pack image URL (AtlListModel/AtlPage) and the share-code
// URL (AtlOptionalModDialog). BuildConfig.ATL_DOWNLOAD_SERVER_URL / ATL_API_BASE_URL become
// settable hooks (tests restore them — see AtLauncherTests for the [Collection] pattern).
using System.Text.Json.Nodes;
using System.Text.Json;
using FlintLauncher.Core.Json;
using FlintLauncher.Core.Net;

namespace FlintLauncher.Core.ModPlatform.AtLauncher;

public static class AtLauncherApi
{
    /// <summary>BuildConfig.ATL_DOWNLOAD_SERVER_URL (settable; tests restore it).</summary>
    public static string DownloadServerUrl { get; set; } = FlintConfig.Default.AtlDownloadServerUrl;

    /// <summary>BuildConfig.ATL_API_BASE_URL (settable; tests restore it).</summary>
    public static string ApiBaseUrl { get; set; } = FlintConfig.Default.AtlApiBaseUrl;

    /// <summary>AtlListModel::loadIndexedPack's packsnew.json URL.</summary>
    public static string PackIndexUrl() => DownloadServerUrl + "launcher/json/packsnew.json";

    /// <summary>PackInstallTask::executeTask's per-version Configs.json URL.</summary>
    public static string PackConfigsUrl(string packSafeName, string versionName) =>
        $"{DownloadServerUrl}packs/{packSafeName}/versions/{versionName}/Configs.json";

    /// <summary>PackInstallTask::installConfigs' Configs.zip URL.</summary>
    public static string PackConfigsZipUrl(string packSafeName, string versionName) =>
        $"{DownloadServerUrl}packs/{packSafeName}/versions/{versionName}/Configs.zip";

    /// <summary>The pack image URL (AtlListModel/AtlPage logo requests).</summary>
    public static string PackImageUrl(string safeName) => $"{DownloadServerUrl}launcher/images/{safeName}";

    /// <summary>AtlOptionalModListModel::useShareCode's share-code URL.</summary>
    public static string ShareCodeUrl(string code) => ApiBaseUrl + "share-codes/" + code;

    // ------------------------------------------------------------------ requests

    /// <summary>makeByteArray(packsnew.json).</summary>
    public static (NetJob Job, NetRequest Request) FetchPackIndex()
    {
        var netJob = new NetJob("ATLauncher::IndexFetch");
        var request = NetRequest.MakeByteArray(new Uri(PackIndexUrl()));
        netJob.AddNetRequest(request);
        return (netJob, request);
    }

    /// <summary>makeByteArray(share-codes/{code}) (AtlOptionalModListModel::useShareCode).</summary>
    public static (NetJob Job, NetRequest Request) FetchShareCode(string code)
    {
        var netJob = new NetJob("Atl::Request");
        var request = NetRequest.MakeByteArray(new Uri(ShareCodeUrl(code)));
        netJob.AddNetRequest(request);
        return (netJob, request);
    }

    // ------------------------------------------------------------------ parsers

    /// <summary>Parses the packsnew.json index (AtlListModel::searchRequestFinished).</summary>
    public static List<IndexedPack> LoadPackIndex(string response)
    {
        var packs = new List<IndexedPack>();
        using var doc = JsonUtils.RequireDocument(response, "JSON response from ATLauncher");
        foreach (var packRaw in AtLauncherJson.Array(doc))
        {
            var pack = new IndexedPack();
            AtLauncherPackIndex.LoadIndexedPack(pack, AtLauncherJson.Object(packRaw));
            packs.Add(pack);
        }
        return packs;
    }

    /// <summary>
    /// Parses a share-code response (AtlOptionalModListModel::shareCodeSuccess); returns
    /// null when the API answered with an error — the message is carried out (upstream
    /// logs "ATLauncher API Response Error").
    /// </summary>
    public static ShareCode? LoadShareCode(string response, out string? errorMessage)
    {
        errorMessage = null;
        using var doc = JsonUtils.RequireDocument(response, "JSON response from ATLauncher");
        var parsed = new ShareCodeResponse();
        AtLauncherShareCode.LoadShareCodeResponse(parsed, AtLauncherJson.Object(doc));
        if (parsed.Error)
        {
            errorMessage = parsed.Message;
            return null;
        }
        return parsed.Data;
    }

    /// <summary>Parses Configs.json into a PackVersion (PackInstallTask::onDownloadSucceeded).</summary>
    public static PackVersion LoadPackVersion(string response)
    {
        using var doc = JsonUtils.RequireDocument(response, "JSON response from ATLauncher");
        var version = new PackVersion();
        AtLauncherPackManifest.LoadVersion(version, AtLauncherJson.Object(doc));
        return version;
    }
}
