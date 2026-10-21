// SPDX-License-Identifier: GPL-3.0-only
// Port of modplatform/atlauncher/ATLShareCode.{h,cpp}: the {AtlApiBaseUrl}share-codes/{code}
// response model (the optional-mod selection encoded by the ATLauncher website). The wire
// shape arrives as the AtlShareCodeResponseJson probe records (source-generated context in
// this file); the loader validates the required members with the Json::require* messages.

using System.Text.Json;

namespace FlintLauncher.Core.ModPlatform.AtLauncher;

/// <summary>The share-codes/{code} wire shape; `message` may be a JSON null.</summary>
public sealed record AtlShareCodeResponseJson(
    bool? Error,
    int? Code,
    string? Message,
    AtlShareCodeJson? Data);

public sealed record AtlShareCodeJson(
    string? Pack,
    string? Version,
    AtlShareCodeModsJson? Mods);

public sealed record AtlShareCodeModsJson(
    List<AtlShareCodeModJson>? Optional);

public sealed record AtlShareCodeModJson(
    bool? Selected,
    string? Name);
// (the AtLauncherJsonContext declaration lives in AtLauncherApi.cs beside the parse
// entries — the STJ source generator cannot declare one context across two files)

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
    public string Message { get; set; } = "";
    public ShareCode Data { get; } = new();
}

public static class AtLauncherShareCode
{
    /// <summary>Port of ATLauncher::loadShareCodeResponse.</summary>
    public static void LoadShareCodeResponse(ShareCodeResponse r, AtlShareCodeResponseJson obj)
    {
        r.Error = Require(obj.Error, "error");
        Require(obj.Code, "code");
        r.Message = obj.Message ?? "";

        if (!r.Error)
        {
            var data = obj.Data ?? throw new JsonException("'data' is not an object");
            r.Data.Pack = Require(data.Pack, "pack");
            r.Data.Version = Require(data.Version, "version");

            var mods = data.Mods ?? throw new JsonException("'mods' is not an object");
            var optional = mods.Optional ?? throw new JsonException("'optional' is not an array");
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
        value ?? throw new JsonException("'" + key + "' is not a string");

    private static T Require<T>(T? value, string key) where T : struct =>
        value ?? throw new JsonException(
            "'" + key + "' is not a " + (typeof(T) == typeof(int) ? "integer" : typeof(T).Name.ToLowerInvariant()));
}
