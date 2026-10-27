// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// The FTB App API surface used by the platform layer: the modpacks.ch index URL
// (/modpack/all -> {"packs":[id,...]}), the per-pack description (/modpack/{id}) and the
// per-version payload (/modpack/{id}/{versionId}).
//
// BuildConfig.FTB_API_BASE_URL became injected instance state: BaseUrl is per-instance
// (tests construct their own client pointed at a stub; the install endpoint derives one
// for its serverUrl override — see FtbEndpoints); the client rides the injected persistent client
// (LauncherRuntime.Ftb — see the Modrinth/Flame de-singletoning for the same story). The
// parsers are pure helpers and stay static; the wire shapes deserialize through the
// source-generated FtbJsonContext (the FTB models carry their annotations themselves).

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using FlintLauncher.Core.Json;
using FlintLauncher.Core.Net;

namespace FlintLauncher.Core.ModPlatform.Ftb;

/// <summary>The /modpack/all wire root; the pack ids coerce like QJsonValue::toInt()
/// (fractional doubles truncate, junk zeroes) through the packs list converter.</summary>
public sealed class FtbPackIndexFile
{
    [JsonConverter(typeof(QtCoercingIntListConverter))]
    public List<int> Packs { get; init; } = [];
}

[JsonSourceGenerationOptions(RespectNullableAnnotations = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FtbPackIndexFile))]
[JsonSerializable(typeof(FtbModpack))]
[JsonSerializable(typeof(FtbVersion))]
internal sealed partial class FtbJsonContext : JsonSerializerContext;

public sealed class FtbApi(HttpClient http)
{
    /// <summary>BuildConfig.FTB_API_BASE_URL (per-instance; tests construct a client
    /// pointed at their stub).</summary>
    public string BaseUrl { get; init; } = FlintConfig.Default.FtbApiBaseUrl;

    /// <summary>The network stack this client's requests go through.</summary>
    public HttpClient Http { get; } = http;


    // ------------------------------------------------------------------ URL builders

    /// <summary>FtbListModel::request's /modpack/all URL.</summary>
    public string ModpacksUrl() => BaseUrl + "/modpack/all";

    /// <summary>FtbListModel::requestPack's /modpack/{id} URL.</summary>
    public string ModpackUrl(int packId) => $"{BaseUrl}/modpack/{packId}";

    /// <summary>PackInstallTask::executeTask's /modpack/{id}/{versionId} URL.</summary>
    public string VersionUrl(int packId, int versionId) => $"{BaseUrl}/modpack/{packId}/{versionId}";

    // ---------------------------------------------------------------------- requests

    /// <summary>makeByteArray(/modpack/all): the raw index payload; failures surface as
    /// <see cref="FlintHttpException"/> (the old NetRequest status gate).</summary>
    public Task<string> FetchModpacksAsync(CancellationToken cancellationToken = default) =>
        FlintHttp.GetStringAsync(Http,
            new HttpRequestMessage(HttpMethod.Get, ModpacksUrl()), cancellationToken);

    /// <summary>makeByteArray(/modpack/{id}).</summary>
    public Task<string> FetchModpackAsync(int packId, CancellationToken cancellationToken = default) =>
        FlintHttp.GetStringAsync(Http,
            new HttpRequestMessage(HttpMethod.Get, ModpackUrl(packId)), cancellationToken);

    /// <summary>makeByteArray(/modpack/{id}/{versionId}).</summary>
    public Task<string> FetchVersionAsync(int packId, int versionId,
        CancellationToken cancellationToken = default) =>
        FlintHttp.GetStringAsync(Http,
            new HttpRequestMessage(HttpMethod.Get, VersionUrl(packId, versionId)), cancellationToken);

    // ----------------------------------------------------------------------- parsers

    /// <summary>Node-side Json::requireDocument(QByteArray, what): JsonNode.Parse with the
    /// parse-error wrapping (the null parse result of a "null" document throws like any
    /// other parse failure).</summary>
    private static JsonNode RequireDocument(string json, string what)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException e)
        {
            throw new JsonException(what + ": Error parsing JSON: " + e.Message);
        }

        return node ?? throw new JsonException(what + ": Error parsing JSON: document is null");
    }

    /// <summary>The shared document root: parse (with the "{what}: Error parsing JSON: ..."
    /// wrapper), guard the object root kind, then deserialize the annotated model. Binding
    /// failures (a missing required key, a malformed member, a JSON null for a required
    /// member) ride the same parse wrapper.</summary>
    private static T LoadRootDocument<T>(string json, JsonTypeInfo<T> typeInfo) where T : class
    {
        var root = RequireDocument(json, "JSON response from FTB");
        if (root is not JsonObject)
        {
            throw new JsonException("Document is not an object");
        }

        T? dto;
        try
        {
            dto = root.Deserialize(typeInfo);
        }
        catch (JsonException e)
        {
            throw new JsonException("JSON response from FTB: Error parsing JSON: " + e.Message);
        }

        return dto ?? throw new JsonException("Document is not an object");
    }

    /// <summary>Parses the /modpack/all index (FtbListModel::requestFinished):
    /// {"packs":[id,...]} -> the list of pack ids. A missing/invalid payload yields an
    /// empty list — the dud-index tolerance of the C++ model.</summary>
    public static List<int> LoadModpackIndex(string response)
    {
        try
        {
            return JsonSerializer.Deserialize(response, FtbJsonContext.Default.FtbPackIndexFile)?.Packs ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Parses a /modpack/{id} response (FtbListModel::packRequestFinished).</summary>
    public static FtbModpack LoadModpack(string response) =>
        LoadRootDocument(response, FtbJsonContext.Default.FtbModpack);

    /// <summary>Parses a /modpack/{id}/{versionId} response (PackInstallTask).</summary>
    public static FtbVersion LoadVersion(string response) =>
        LoadRootDocument(response, FtbJsonContext.Default.FtbVersion);
}
