// SPDX-License-Identifier: GPL-3.0-only
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

public sealed class FtbAppPaths(string? rootPath = null)
{
    /// <summary>The FTB App root (getFTBRoot()); injected.</summary>
    public string RootPath { get; set; } = rootPath ?? DefaultRoot();

    /// <summary>Port of getFTBRoot: ~/.ftba, under Library/Application Support on macOS.</summary>
    public static string DefaultRoot()
    {
        var partialPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
        {
            partialPath = Path.Combine(partialPath, "Library/Application Support");
        }

        return Path.Combine(partialPath, ".ftba");
    }

    /// <summary>Port of getDynamicPath: the "instanceLocation" value of the FTB App
    /// settings.json (storage/, then bin/); "" when neither file exists or cannot be
    /// parsed.</summary>
    public string GetDynamicInstancesPath()
    {
        var settingsPath = Path.Combine(RootPath, "storage", "settings.json");
        if (!File.Exists(settingsPath))
        {
            settingsPath = Path.Combine(RootPath, "bin", "settings.json");
        }

        if (!File.Exists(settingsPath))
        {
            return "";
        }

        try
        {
            var settings = JsonSerializer.Deserialize(File.ReadAllBytes(settingsPath),
                FtbAppSettingsJsonContext.Default.FtbAppSettingsJson);
            return settings?.InstanceLocation ?? "";
        }
        catch (Exception e) when (e is JsonException)
        {
            return "";
        }
    }
}
