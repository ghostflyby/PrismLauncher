// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// The ATL wire decode: the source-generated context of the wire models and the
// response-body entry points (binding failures carry the "JSON response from ATLauncher:
// Error parsing JSON: ..." wrapper, kept for the surfaced "Could not understand ..."
// failure messages).

using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlintLauncher.ExternalApi.AtLauncher;

/// <summary>The source-generated context of the ATL wire models (the share-code probe, the
/// packsnew.json index and the Configs.json PackVersion); the models carry their own
/// [JsonPropertyName] annotations and the QJsonValue-semantics converters.</summary>
[JsonSourceGenerationOptions(RespectNullableAnnotations = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AtlShareCodeResponseJson))]
[JsonSerializable(typeof(List<IndexedPack>))]
[JsonSerializable(typeof(PackVersion))]
public sealed partial class AtLauncherJsonContext : JsonSerializerContext;

/// <summary>The strict document decode of the ATL payloads — the entry points the
/// operations read responses through and callers reuse for out-of-band payloads.</summary>
public static class AtLauncherJson
{
    /// <summary>Parses the packsnew.json index (AtlListModel::searchRequestFinished);
    /// binding failures ride the parse wrapper.</summary>
    public static List<IndexedPack> LoadPackIndex(string response)
    {
        try
        {
            return JsonSerializer.Deserialize(response, AtLauncherJsonContext.Default.ListIndexedPack) ?? [];
        }
        catch (JsonException e)
        {
            throw new JsonException("JSON response from ATLauncher: Error parsing JSON: " + e.Message, e);
        }
    }

    /// <summary>Parses a share-code response (AtlOptionalModListModel::shareCodeSuccess) into
    /// the validated response: an API-side error is carried in
    /// <see cref="ShareCodeResponse.Error"/>/<see cref="ShareCodeResponse.Message"/>, a
    /// malformed document throws.</summary>
    public static ShareCodeResponse LoadShareCodeResponse(string response)
    {
        AtlShareCodeResponseJson? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(response, AtLauncherJsonContext.Default.AtlShareCodeResponseJson);
        }
        catch (JsonException e)
        {
            throw new JsonException("JSON response from ATLauncher: Error parsing JSON: " + e.Message, e);
        }

        return AtLauncherShareCode.LoadShareCodeResponse(
            parsed ?? throw new JsonException(
                "JSON response from ATLauncher: Error parsing JSON: document is null"));
    }

    /// <summary>
    /// Parses a share-code response; returns null when the API answered with an error — the
    /// message is carried out (upstream logs "ATLauncher API Response Error").
    /// </summary>
    public static ShareCode? LoadShareCode(string response, out string? errorMessage)
    {
        var shareCodeResponse = LoadShareCodeResponse(response);
        if (shareCodeResponse.Error)
        {
            errorMessage = shareCodeResponse.Message;
            return null;
        }

        errorMessage = null;
        return shareCodeResponse.Data!;
    }

    /// <summary>Parses Configs.json into a PackVersion (PackInstallTask::onDownloadSucceeded);
    /// runs the load-time fixups (the Minecraft-Forge-as-jar correction, the extractFolder
    /// %s% rewrite) after the deserialization.</summary>
    public static PackVersion LoadPackVersion(string response)
    {
        PackVersion? version;
        try
        {
            version = JsonSerializer.Deserialize(response, AtLauncherJsonContext.Default.PackVersion);
        }
        catch (JsonException e)
        {
            throw new JsonException("JSON response from ATLauncher: Error parsing JSON: " + e.Message, e);
        }

        if (version is null)
        {
            throw new JsonException("Value is not an object");
        }

        AtLauncherPackManifest.Normalize(version);
        return version;
    }
}
