// SPDX-License-Identifier: GPL-3.0-only
// Port of modplatform/atlauncher/ATLShareCode.{h,cpp}: the {AtlApiBaseUrl}share-codes/{code}
// response model (the optional-mod selection encoded by the ATLauncher website). The wire
// shape arrives as the AtlShareCodeResponseJson probe records (source-generated context in
// this file); the loader validates the required members with the Json::require* messages.

using System.Text.Json;
using System.Text.Json.Serialization;
using FlintLauncher.Core.Json;

namespace FlintLauncher.Core.ModPlatform.AtLauncher;

/// <summary>The share-codes/{code} wire shape; `message` may be a JSON null.</summary>
public sealed record AtlShareCodeResponseJson(
    [property: JsonPropertyName("error")] bool? Error,
    [property: JsonPropertyName("code")] int? Code,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("data")] AtlShareCodeJson? Data);

public sealed record AtlShareCodeJson(
    [property: JsonPropertyName("pack")] string? Pack,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("mods")] AtlShareCodeModsJson? Mods);

public sealed record AtlShareCodeModsJson(
    [property: JsonPropertyName("optional")] List<AtlShareCodeModJson>? Optional);

public sealed record AtlShareCodeModJson(
    [property: JsonPropertyName("selected")] bool? Selected,
    [property: JsonPropertyName("name")] string? Name);

[JsonSerializable(typeof(AtlShareCodeResponseJson))]
internal sealed partial class AtLauncherJsonContext : JsonSerializerContext;

public sealed class ShareCodeMod
{
    public bool Selected { get; set; }
    public string Name { get; set; } = "";
}

public sealed class ShareCode
{
    public string Pack { get; set; } = "";
    public string Version { get; set; } = "";
    public List<ShareCodeMod> Mods { get; } = [];
}

public sealed class ShareCodeResponse
{
    public bool Error { get; set; }
    public int Code { get; set; }
    public string Message { get; set; } = "";
    public ShareCode Data { get; } = new();
}

public static class AtLauncherShareCode
{
    /// <summary>Port of ATLauncher::loadShareCodeResponse.</summary>
    public static void LoadShareCodeResponse(ShareCodeResponse r, AtlShareCodeResponseJson obj)
    {
        r.Error = Require(obj.Error, "error");
        r.Code = Require(obj.Code, "code");
        r.Message = obj.Message ?? "";

        if (!r.Error)
        {
            var data = obj.Data ?? throw new JsonParseException("'data' is not an object");
            r.Data.Pack = Require(data.Pack, "pack");
            r.Data.Version = Require(data.Version, "version");

            var mods = data.Mods ?? throw new JsonParseException("'mods' is not an object");
            var optional = mods.Optional ?? throw new JsonParseException("'optional' is not an array");
            foreach (var mod in optional)
            {
                r.Data.Mods.Add(new ShareCodeMod
                {
                    Selected = Require(mod.Selected, "selected"),
                    Name = Require(mod.Name, "name"),
                });
            }
        }
    }

    private static string Require(string? value, string key) =>
        value ?? throw new JsonParseException("'" + key + "' is not a string");

    private static T Require<T>(T? value, string key) where T : struct =>
        value ?? throw new JsonParseException(
            "'" + key + "' is not a " + (typeof(T) == typeof(int) ? "integer" : typeof(T).Name.ToLowerInvariant()));
}
