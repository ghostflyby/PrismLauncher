// SPDX-License-Identifier: GPL-3.0-only
// Port of modplatform/technic/SolderPackManifest.{h,cpp}: the Solder API models —
// /modpack/{slug} (recommended/latest/builds) and /modpack/{slug}/{build} (minecraft + mods).
using System.Text.Json.Nodes;
using FlintLauncher.Core.Json;

namespace FlintLauncher.Core.ModPlatform.Technic;

public sealed class SolderPack
{
    public string Recommended { get; set; } = "";
    public string Latest { get; set; } = "";
    public List<string> Builds { get; } = [];
}

public sealed class SolderPackBuildMod
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Md5 { get; set; } = "";
    public string Url { get; set; } = "";
}

public sealed class SolderPackBuild
{
    public string Minecraft { get; set; } = "";
    public List<SolderPackBuildMod> Mods { get; } = [];
}

public static class TechnicSolder
{
    /// <summary>Port of TechnicSolder::loadPack.</summary>
    public static void LoadPack(SolderPack v, JsonObject obj)
    {
        v.Recommended = JsonUtils.RequireString(obj, "recommended");
        v.Latest = JsonUtils.RequireString(obj, "latest");

        var builds = TechnicJson.RequireArray(obj, "builds");
        foreach (var buildRaw in builds)
        {
            v.Builds.Add(buildRaw is JsonValue buildValue && buildValue.TryGetValue<string>(out var build)
                ? build
                : throw new JsonParseException("Value is not a string"));
        }
    }

    private static void LoadPackBuildMod(SolderPackBuildMod b, JsonObject obj)
    {
        b.Name = JsonUtils.RequireString(obj, "name");
        b.Version = obj.TryGetPropertyValue("version", out var versionNode) && versionNode is JsonValue versionValue
            && versionValue.TryGetValue<string>(out var version) ? version : "";
        b.Md5 = JsonUtils.RequireString(obj, "md5");
        b.Url = JsonUtils.RequireString(obj, "url");
    }

    /// <summary>Port of TechnicSolder::loadPackBuild.</summary>
    public static void LoadPackBuild(SolderPackBuild v, JsonObject obj)
    {
        v.Minecraft = JsonUtils.RequireString(obj, "minecraft");

        var mods = TechnicJson.RequireArray(obj, "mods");
        foreach (var modRaw in mods)
        {
            var mod = new SolderPackBuildMod();
            LoadPackBuildMod(mod, (JsonObject)(modRaw ?? throw new JsonParseException("Value is not an object")));
            v.Mods.Add(mod);
        }
    }
}
