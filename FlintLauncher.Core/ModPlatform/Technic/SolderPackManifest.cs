// SPDX-License-Identifier: GPL-3.0-only
// Port of modplatform/technic/SolderPackManifest.{h,cpp}: the Solder API models —
// /modpack/{slug} (recommended/latest/builds) and /modpack/{slug}/{build} (minecraft + mods).
// Reads go through the JsonNode model (single-DOM).

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
    public static void LoadPack(SolderPack v, JsonNode obj)
    {
        v.Recommended = JsonUtils.RequireString(obj, "recommended", "'recommended'");
        v.Latest = JsonUtils.RequireString(obj, "latest", "'latest'");

        var builds = JsonUtils.RequireArray(obj, "builds", "'builds'");
        foreach (var buildRaw in builds)
        {
            v.Builds.Add(buildRaw is JsonValue buildValue && buildValue.TryGetValue<string>(out var build)
                ? build
                : throw new JsonParseException("Value is not a string"));
        }
    }

    private static void LoadPackBuildMod(SolderPackBuildMod b, JsonNode obj)
    {
        b.Name = JsonUtils.RequireString(obj, "name", "'name'");
        b.Version = JsonUtils.EnsureString(obj["version"]);
        b.Md5 = JsonUtils.RequireString(obj, "md5", "'md5'");
        b.Url = JsonUtils.RequireString(obj, "url", "'url'");
    }

    /// <summary>Port of TechnicSolder::loadPackBuild.</summary>
    public static void LoadPackBuild(SolderPackBuild v, JsonNode obj)
    {
        v.Minecraft = JsonUtils.RequireString(obj, "minecraft", "'minecraft'");

        var mods = JsonUtils.RequireArray(obj, "mods", "'mods'");
        foreach (var modRaw in mods)
        {
            var mod = new SolderPackBuildMod();
            // the downcast keeps the old hard-cast behavior for a non-object entry
            LoadPackBuildMod(mod, (JsonObject)(modRaw ?? throw new JsonParseException("Value is not an object")));
            v.Mods.Add(mod);
        }
    }
}
