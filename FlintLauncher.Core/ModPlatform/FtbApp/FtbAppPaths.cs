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
using System.Text.Json.Nodes;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Json;

namespace FlintLauncher.Core.ModPlatform.FtbApp;

public sealed class FtbAppPaths
{
    public FtbAppPaths(string? rootPath = null)
    {
        RootPath = rootPath ?? DefaultRoot();
    }

    /// <summary>The FTB App root (getFTBRoot()); injected.</summary>
    public string RootPath { get; set; }

    /// <summary>Port of getFTBRoot: ~/.ftba, under Library/Application Support on macOS.</summary>
    public static string DefaultRoot()
    {
        var partialPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
        {
            partialPath = FileSystem.PathCombine(partialPath, "Library/Application Support");
        }
        return FileSystem.PathCombine(partialPath, ".ftba");
    }

    /// <summary>Port of getDynamicPath: the "instanceLocation" value of the FTB App
    /// settings.json (storage/, then bin/); "" when neither file exists or cannot be
    /// parsed.</summary>
    public string GetDynamicInstancesPath()
    {
        var settingsPath = FileSystem.PathCombine(RootPath, "storage", "settings.json");
        if (!File.Exists(settingsPath))
        {
            settingsPath = FileSystem.PathCombine(RootPath, "bin", "settings.json");
        }
        if (!File.Exists(settingsPath))
        {
            return "";
        }
        try
        {
            var root = JsonUtils.RequireNodeFromFile(settingsPath, "FTB App settings file");
            return root["instanceLocation"] is JsonValue value && value.TryGetValue<string>(out var location)
                ? location : "";
        }
        catch (Exception e) when (e is JsonParseException or JsonException)
        {
            return "";
        }
    }
}
