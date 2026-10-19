// SPDX-License-Identifier: GPL-3.0-only
// The ATLauncher API surface used by the platform layer: the packsnew.json index URL
// (AtlListModel), the per-pack/version Configs.json + Configs.zip and mod/library download
// URLs (ATLPackInstallTask), the pack image URL (AtlListModel/AtlPage) and the share-code
// URL (AtlOptionalModDialog).
//
// BuildConfig.ATL_DOWNLOAD_SERVER_URL / ATL_API_BASE_URL became injected instance state:
// DownloadServerUrl / ApiBaseUrl are per-instance (tests construct their own client pointed
// at a stub); the client rides the injected NetStack (LauncherRuntime.AtLauncher — see the
// Modrinth/Flame/Technic de-singletoning for the same story). The parsers are pure helpers
// and stay static; the wire shapes deserialize through the source-generated
// AtLauncherJsonContext (the ATL models carry their own annotations; the document parse of
// each entry keeps the "{what}: Error parsing JSON: ..." wrapper via AtLauncherJson).

using System.Text.Json;
using System.Text.Json.Serialization;
using FlintLauncher.Core.Json;
using FlintLauncher.Core.Net;

namespace FlintLauncher.Core.ModPlatform.AtLauncher;

/// <summary>The source-generated context of the ATL wire models (the share-code probe, the
/// packsnew.json index and the Configs.json PackVersion); the models carry their own
/// [JsonPropertyName] annotations and the QJsonValue-semantics converters.</summary>
[JsonSourceGenerationOptions(RespectNullableAnnotations = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AtlShareCodeResponseJson))]
[JsonSerializable(typeof(List<IndexedPack>))]
[JsonSerializable(typeof(PackVersion))]
internal sealed partial class AtLauncherJsonContext : JsonSerializerContext;

public sealed class AtLauncherApi(NetStack? net = null)
{
    /// <summary>BuildConfig.ATL_DOWNLOAD_SERVER_URL (per-instance; tests construct a client
    /// pointed at their stub).</summary>
    public string DownloadServerUrl { get; set; } = FlintConfig.Default.AtlDownloadServerUrl;

    /// <summary>BuildConfig.ATL_API_BASE_URL (per-instance).</summary>
    public string ApiBaseUrl { get; set; } = FlintConfig.Default.AtlApiBaseUrl;

    /// <summary>The network stack this client's requests go through.</summary>
    public NetStack Net { get; } = net ?? NetStack.Default;

    // ------------------------------------------------------------------ URL builders

    /// <summary>AtlListModel::loadIndexedPack's packsnew.json URL.</summary>
    public string PackIndexUrl() => DownloadServerUrl + "launcher/json/packsnew.json";

    /// <summary>PackInstallTask::executeTask's per-version Configs.json URL.</summary>
    public string PackConfigsUrl(string packSafeName, string versionName) =>
        $"{DownloadServerUrl}packs/{packSafeName}/versions/{versionName}/Configs.json";

    /// <summary>PackInstallTask::installConfigs' Configs.zip URL.</summary>
    public string PackConfigsZipUrl(string packSafeName, string versionName) =>
        $"{DownloadServerUrl}packs/{packSafeName}/versions/{versionName}/Configs.zip";

    /// <summary>The pack image URL (AtlListModel/AtlPage logo requests).</summary>
    public string PackImageUrl(string safeName) => $"{DownloadServerUrl}launcher/images/{safeName}";

    /// <summary>AtlOptionalModListModel::useShareCode's share-code URL.</summary>
    public string ShareCodeUrl(string code) => ApiBaseUrl + "share-codes/" + code;

    // ------------------------------------------------------------------ requests

    /// <summary>makeByteArray(packsnew.json).</summary>
    public (NetJob Job, NetRequest Request) FetchPackIndex()
    {
        var netJob = new NetJob("ATLauncher::IndexFetch");
        var request = NetRequest.MakeByteArray(new Uri(PackIndexUrl()), stack: Net);
        netJob.AddNetRequest(request);
        return (netJob, request);
    }

    /// <summary>makeByteArray(share-codes/{code}) (AtlOptionalModListModel::useShareCode).</summary>
    public (NetJob Job, NetRequest Request) FetchShareCode(string code)
    {
        var netJob = new NetJob("Atl::Request");
        var request = NetRequest.MakeByteArray(new Uri(ShareCodeUrl(code)), stack: Net);
        netJob.AddNetRequest(request);
        return (netJob, request);
    }

    // ------------------------------------------------------------------ parsers

    /// <summary>Parses the packsnew.json index (AtlListModel::searchRequestFinished).</summary>
    public static List<IndexedPack> LoadPackIndex(string response)
    {
        var root = AtLauncherJson.RequireDocument(response, "JSON response from ATLauncher");
        try
        {
            return root.Deserialize(AtLauncherJsonContext.Default.ListIndexedPack) ?? [];
        }
        catch (JsonException e)
        {
            // binding failures (a non-array root, a non-object entry, a missing required
            // member) keep the parse wrapper of the former per-entry Require reads
            throw new JsonParseException("JSON response from ATLauncher: Error parsing JSON: " + e.Message);
        }
    }

    /// <summary>
    /// Parses a share-code response (AtlOptionalModListModel::shareCodeSuccess); returns
    /// null when the API answered with an error — the message is carried out (upstream
    /// logs "ATLauncher API Response Error").
    /// </summary>
    public static ShareCode? LoadShareCode(string response, out string? errorMessage)
    {
        errorMessage = null;
        AtlShareCodeResponseJson parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(response, AtLauncherJsonContext.Default.AtlShareCodeResponseJson)
                     ?? throw new JsonParseException(
                         "JSON response from ATLauncher: Error parsing JSON: document is null");
        }
        catch (JsonException e)
        {
            throw new JsonParseException("JSON response from ATLauncher: Error parsing JSON: " + e.Message);
        }

        var shareCodeResponse = new ShareCodeResponse();
        AtLauncherShareCode.LoadShareCodeResponse(shareCodeResponse, parsed);
        if (shareCodeResponse.Error)
        {
            errorMessage = shareCodeResponse.Message;
            return null;
        }

        return shareCodeResponse.Data;
    }

    /// <summary>Parses Configs.json into a PackVersion (PackInstallTask::onDownloadSucceeded);
    /// runs the load-time fixups (the Minecraft-Forge-as-jar correction, the extractFolder
    /// %s% rewrite) after the deserialization.</summary>
    public static PackVersion LoadPackVersion(string response)
    {
        var root = AtLauncherJson.RequireDocument(response, "JSON response from ATLauncher");
        PackVersion? version;
        try
        {
            version = root.Deserialize(AtLauncherJsonContext.Default.PackVersion)
                      ?? throw new JsonParseException("Value is not an object");
        }
        catch (JsonException e)
        {
            throw new JsonParseException("JSON response from ATLauncher: Error parsing JSON: " + e.Message);
        }

        AtLauncherPackManifest.Normalize(version);
        return version;
    }
}
