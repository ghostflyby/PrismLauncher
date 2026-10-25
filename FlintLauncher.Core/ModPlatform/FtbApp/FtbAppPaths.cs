// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2023 Trial97 <alexandru.tripon97@gmail.com>
//
// Port of the FTB App root-path discovery of ui/pages/modplatform/import_ftb/ListModel.cpp
// (getFTBRoot/getDynamicPath): the FTB App root is ~/.ftba (macOS: under Library/
// Application Support) and the instances folder comes from the "instanceLocation" key of
// storage/settings.json (fallback bin/settings.json).
//
// Deviation: the launcher-wide "FTBAppInstancesPath" setting and the QProcessEnvironment
// lookups of the original are UI concerns, so RootPath is injected per instance (tests
// construct an FtbAppPaths pointed at their tree); DefaultRoot stays a pure static helper.

using System.Text.Json;
using System.Text.Json.Serialization;
using FlintLauncher.Core.Json;
using TruePath;
using TruePath.SystemIo;

namespace FlintLauncher.Core.ModPlatform.FtbApp;

/// <summary>The FTB App settings.json wire: only instanceLocation is consumed; unknown
/// members of the third-party document are skipped.</summary>
internal sealed record FtbAppSettingsJson
{
    [JsonConverter(typeof(QtCoercingNullableStringConverter))]
    public string? InstanceLocation { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FtbAppSettingsJson))]
internal sealed partial class FtbAppSettingsJsonContext : JsonSerializerContext;

public sealed class FtbAppPaths(AbsolutePath? rootPath = null)
{
    /// <summary>The FTB App root (getFTBRoot()); injected.</summary>
    public AbsolutePath RootPath { get; set; } = rootPath ?? DefaultRoot();

    /// <summary>Port of getFTBRoot: ~/.ftba, under Library/Application Support on macOS.</summary>
    public static AbsolutePath DefaultRoot()
    {
        var partialPath = new AbsolutePath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (OperatingSystem.IsMacOS())
        {
            partialPath = partialPath / "Library/Application Support";
        }

        return partialPath / ".ftba";
    }

    /// <summary>Port of getDynamicPath: the "instanceLocation" value of the FTB App
    /// settings.json (storage/, then bin/); "" when neither file exists or cannot be
    /// parsed.</summary>
    public string GetDynamicInstancesPath()
    {
        var settingsPath = RootPath / "storage" / "settings.json";
        if (!settingsPath.ExistsFile())
        {
            settingsPath = RootPath / "bin" / "settings.json";
        }

        if (!settingsPath.ExistsFile())
        {
            return "";
        }

        try
        {
            var settings = JsonSerializer.Deserialize(settingsPath.ReadAllBytes(),
                FtbAppSettingsJsonContext.Default.FtbAppSettingsJson);
            return settings?.InstanceLocation ?? "";
        }
        catch (Exception e) when (e is JsonException)
        {
            return "";
        }
    }
}
