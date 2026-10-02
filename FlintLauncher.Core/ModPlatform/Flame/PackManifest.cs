// SPDX-License-Identifier: GPL-3.0-only
// Port of modplatform/flame/PackManifest.{h,cpp}: the CurseForge modpack manifest.json
// model (manifestType "minecraftModpack", manifestVersion 1) and its loader. The C++
// QMap<int, File> (key-sorted) becomes a SortedDictionary keyed by file id.
using System.Text.Json.Nodes;
using FlintLauncher.Core.Json;

namespace FlintLauncher.Core.ModPlatform.Flame;

/// <summary>Port of Flame::File.</summary>
public sealed class FlameFile
{
    public long ProjectId { get; set; }
    public long FileId { get; set; }

    // NOTE: the opposite to 'optional'
    public bool Required { get; set; } = true;

    public IndexedPack Pack { get; } = new();
    public IndexedVersion Version { get; set; } = new();

    // our
    public string TargetFolder { get; set; } = "mods";
}

/// <summary>Port of Flame::Modloader.</summary>
public sealed class FlameModloader
{
    public string Id { get; set; } = "";
    public bool Primary { get; set; }
}

/// <summary>Port of Flame::Minecraft.</summary>
public sealed class FlameMinecraft
{
    public string Version { get; set; } = "";
    public string Libraries { get; set; } = "";
    public List<FlameModloader> ModLoaders { get; } = [];
    public int RecommendedRam { get; set; }
}

/// <summary>Port of Flame::Manifest.</summary>
public sealed class FlameManifest
{
    public string ManifestType { get; set; } = "";
    public int ManifestVersion { get; set; }
    public FlameMinecraft Minecraft { get; } = new();
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Author { get; set; } = "";

    // File id -> File (the C++ QMap iterates in key order)
    public SortedDictionary<long, FlameFile> Files { get; } = new();

    public string Overrides { get; set; } = "";

    public bool IsLoaded { get; set; }

    /// <summary>Port of Flame::loadManifest: parse the manifest.json at <paramref name="filepath"/>.
    /// Throws <see cref="JsonParseException"/> for non-modpack manifests and unknown versions.</summary>
    public static void LoadManifest(FlameManifest m, string filepath)
    {
        if (JsonUtils.RequireNodeFromFile(filepath, "manifest.json") is not JsonObject obj)
        {
            throw new JsonParseException("manifest.json is not an object");
        }

        m.ManifestType = RequireString(obj, "manifestType");
        if (m.ManifestType != "minecraftModpack")
        {
            throw new JsonParseException("Not a modpack manifest!");
        }
        m.ManifestVersion = RequireInteger(obj, "manifestVersion");
        if (m.ManifestVersion != 1)
        {
            throw new JsonParseException($"Unknown manifest version ({m.ManifestVersion})");
        }
        LoadManifestV1(m, obj);
    }

    private static void LoadFileV1(FlameFile f, JsonObject file)
    {
        f.ProjectId = RequireInteger(file, "projectID");
        f.FileId = RequireInteger(file, "fileID");
        f.Required = OptBoolean(file, "required", defaultValue: true);
    }

    private static void LoadModloaderV1(FlameModloader m, JsonObject modLoader)
    {
        m.Id = RequireString(modLoader, "id");
        m.Primary = OptBoolean(modLoader, "primary");
    }

    private static void LoadMinecraftV1(FlameMinecraft m, JsonObject minecraft)
    {
        m.Version = RequireString(minecraft, "version");
        // extra libraries... apparently only used for a custom Minecraft launcher in the
        // 1.2.5 FTB retro pack; intended use is likely hardcoded in the 'Flame' client,
        // the manifest says nothing
        m.Libraries = OptString(minecraft, "libraries");
        if (minecraft["modLoaders"] is JsonArray arr)
        {
            foreach (var item in arr)
            {
                var loader = new FlameModloader();
                LoadModloaderV1(loader, RequireObject(item!));
                m.ModLoaders.Add(loader);
            }
        }
        m.RecommendedRam = OptInteger(minecraft, "recommendedRam");
    }

    private static void LoadManifestV1(FlameManifest pack, JsonObject manifest)
    {
        var mc = JsonUtils.RequireObject(manifest, "minecraft");
        LoadMinecraftV1(pack.Minecraft, mc);

        pack.Name = OptString(manifest, "name", "Unnamed");
        pack.Version = OptString(manifest, "version");
        pack.Author = OptString(manifest, "author", "Anonymous");

        if (manifest["files"] is JsonArray files)
        {
            foreach (var item in files)
            {
                var file = new FlameFile();
                LoadFileV1(file, RequireObject(item!));
                pack.Files[file.FileId] = file;
            }
        }

        pack.Overrides = OptString(manifest, "overrides", "overrides");

        pack.IsLoaded = true;
    }

    // ------------------------------------------------- QJsonObject-style helpers

    private static JsonNode? Opt(JsonObject obj, string key)
    {
        return obj.TryGetPropertyValue(key, out var node) ? node : null;
    }

    private static string OptString(JsonObject obj, string key, string def = "")
    {
        return Opt(obj, key) is JsonValue value && value.TryGetValue<string>(out var s) ? s : def;
    }

    private static bool OptBoolean(JsonObject obj, string key, bool defaultValue = false)
    {
        return Opt(obj, key) is JsonValue value && value.TryGetValue<bool>(out var b) ? b : defaultValue;
    }

    private static int OptInteger(JsonObject obj, string key)
    {
        if (Opt(obj, key) is not JsonValue value)
        {
            return 0;
        }
        // JSON numbers are doubles; mirror the QJsonValue::toInt truncation
        return value.TryGetValue<double>(out var d) ? (int)d : 0;
    }

    private static string RequireString(JsonObject parent, string key)
    {
        return JsonUtils.RequireString(parent, key);
    }

    private static int RequireInteger(JsonObject parent, string key)
    {
        return JsonUtils.RequireInteger(parent, key);
    }

    private static JsonObject RequireObject(JsonNode node)
    {
        return node is JsonObject obj
            ? obj
            : throw new JsonParseException("Value is not an object");
    }
}
