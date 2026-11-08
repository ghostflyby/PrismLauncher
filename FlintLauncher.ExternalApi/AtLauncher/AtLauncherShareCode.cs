// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022 Jamie Mansfield <jmansfield@cadixdev.org>
//
// Port of modplatform/atlauncher/ATLShareCode.{h,cpp}: the {ApiBaseUrl}share-codes/{code}
// response model (the optional-mod selection encoded by the ATLauncher website). The wire
// shape arrives as the AtlShareCodeResponseJson probe records; the loader validates the
// required members with the Json::require* messages.

using System.Text.Json;

namespace FlintLauncher.ExternalApi.AtLauncher;

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
// (the AtLauncherJsonContext declaration lives in AtLauncherJson.cs beside the parse
// entries — the STJ source generator cannot declare one context across two files)

public sealed record ShareCodeMod(bool Selected, string Name);

public sealed record ShareCode(string Pack, string Version, IReadOnlyList<ShareCodeMod> Mods);

/// <summary>The validated share-code answer: <see cref="Message"/> is null unless the API
/// side refused.</summary>
public sealed record ShareCodeResponse(bool Error, string? Message, ShareCode? Data);

public static class AtLauncherShareCode
{
    /// <summary>Port of ATLauncher::loadShareCodeResponse.</summary>
    public static ShareCodeResponse LoadShareCodeResponse(AtlShareCodeResponseJson obj)
    {
        var error = Require(obj.Error, "error");
        Require(obj.Code, "code");
        if (error)
        {
            return new ShareCodeResponse(true, obj.Message, null);
        }

        var data = obj.Data ?? throw new JsonException("'data' is not an object");
        var pack = Require(data.Pack, "pack");
        var version = Require(data.Version, "version");

        var mods = data.Mods ?? throw new JsonException("'mods' is not an object");
        var optional = mods.Optional ?? throw new JsonException("'optional' is not an array");
        var selections = new List<ShareCodeMod>(optional.Count);
        foreach (var mod in optional)
        {
            selections.Add(new ShareCodeMod(
                Require(mod.Selected, "selected"),
                Require(mod.Name, "name")));
        }

        return new ShareCodeResponse(false, null, new ShareCode(pack, version, selections));
    }

    private static string Require(string? value, string key) =>
        value ?? throw new JsonException("'" + key + "' is not a string");

    private static T Require<T>(T? value, string key) where T : struct =>
        value ?? throw new JsonException(
            "'" + key + "' is not a " + (typeof(T) == typeof(int) ? "integer" : typeof(T).Name.ToLowerInvariant()));
}
