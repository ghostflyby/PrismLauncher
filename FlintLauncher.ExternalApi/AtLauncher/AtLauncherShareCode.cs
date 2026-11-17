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
public sealed record AtlShareCodeResponseJson
{
    public required bool Error { get; init; }

    public required int Code { get; init; }

    public string? Message { get; init; }

    public AtlShareCodeJson? Data { get; init; }
}

public sealed record AtlShareCodeJson
{
    public required string Pack { get; init; }

    public required string Version { get; init; }

    public required AtlShareCodeModsJson Mods { get; init; }
}

public sealed record AtlShareCodeModsJson
{
    public required List<AtlShareCodeModJson> Optional { get; init; }
}

public sealed record AtlShareCodeModJson
{
    public required bool Selected { get; init; }

    public required string Name { get; init; }
}
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
        if (obj.Error)
        {
            return new ShareCodeResponse(true, obj.Message, null);
        }

        var data = obj.Data ?? throw new JsonException("'data' is not an object");
        var selections = new List<ShareCodeMod>(data.Mods.Optional.Count);
        foreach (var rawMod in data.Mods.Optional)
        {
            var mod = rawMod ?? throw new JsonException("Value is not an object");
            selections.Add(new ShareCodeMod(mod.Selected, mod.Name));
        }

        return new ShareCodeResponse(false, null, new ShareCode(data.Pack, data.Version, selections));
    }
}
