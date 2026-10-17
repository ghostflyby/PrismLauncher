// SPDX-License-Identifier: GPL-3.0-only
// Port of modplatform/atlauncher/ATLPackManifest.{h,cpp}: the per-pack/version Configs.json
// model (loader, libraries, mods incl. optional flags, configs checksum, keeps/deletes,
// mainClass/extraArguments) with the exact fields the C++ loadVersion() reads. Reads go
// through the JsonNode model (single-DOM, the JsonUtils node-side family).

using System.Text.Json.Nodes;
using FlintLauncher.Core.Json;

namespace FlintLauncher.Core.ModPlatform.AtLauncher;

/// <summary>Port of ATLauncher::PackType.</summary>
public enum PackType : byte
{
    Public,
    Private,
}

/// <summary>Port of ATLauncher::ModType (https://wiki.atlauncher.com/mod_types).</summary>
public enum ModType : byte
{
    Root,
    Forge,
    Jar,
    Mods,
    Flan,
    Dependency,
    Ic2Lib,
    DenLib,
    Coremods,
    Mcpc,
    Plugins,
    Extract,
    Decomp,
    TexturePack,
    ResourcePack,
    ShaderPack,
    TexturePackExtract,
    ResourcePackExtract,
    Millenaire,
    Unknown,
}

/// <summary>Port of ATLauncher::DownloadType.</summary>
public enum DownloadType : byte
{
    Server,
    Browser,
    Direct,
    Unknown,
}

public sealed class VersionLoader
{
    public string Type { get; set; } = "";
    public bool Latest { get; set; }
    public bool Recommended { get; set; }
    public bool Choose { get; set; }

    public string Version { get; set; } = "";
}

public sealed class VersionLibrary
{
    public string Url { get; set; } = "";
    public string File { get; set; } = "";
    public string Server { get; set; } = "";
    public string Md5 { get; set; } = "";
    public DownloadType Download { get; set; }
    public string DownloadRaw { get; set; } = "";
}

public sealed class VersionMod
{
    public string Name { get; set; } = "";
    public string ModVersion { get; set; } = "";
    public string Url { get; set; } = "";
    public string File { get; set; } = "";
    public string Md5 { get; set; } = "";
    public DownloadType Download { get; set; }
    public string DownloadRaw { get; set; } = "";
    public ModType Type { get; set; }
    public string TypeRaw { get; set; } = "";

    public ModType ExtractTo { get; set; }
    public string ExtractToRaw { get; set; } = "";
    public string ExtractFolder { get; set; } = "";

    public ModType DecompType { get; set; }
    public string DecompTypeRaw { get; set; } = "";
    public string DecompFile { get; set; } = "";

    public string Description { get; set; } = "";
    public bool Optional { get; set; }
    public bool Recommended { get; set; }
    public bool Selected { get; set; }
    public bool Hidden { get; set; }
    public bool Library { get; set; }
    public string Group { get; set; } = "";
    public List<string> Depends { get; } = [];
    public string Colour { get; set; } = "";
    public string Warning { get; set; } = "";

    public bool Client { get; set; }

    // computed
    public bool EffectivelyHidden { get; set; }
}

public sealed class VersionConfigs
{
    public int Filesize { get; set; }
    public string Sha1 { get; set; } = "";
}

public sealed class VersionMessages
{
    public string Install { get; set; } = "";
    public string Update { get; set; } = "";
}

public sealed class VersionKeep
{
    public string Base { get; set; } = "";
    public string Target { get; set; } = "";
}

public sealed class VersionKeeps
{
    public List<VersionKeep> Files { get; } = [];
    public List<VersionKeep> Folders { get; } = [];
}

public sealed class VersionDelete
{
    public string Base { get; set; } = "";
    public string Target { get; set; } = "";
}

public sealed class VersionDeletes
{
    public List<VersionDelete> Files { get; } = [];
    public List<VersionDelete> Folders { get; } = [];
}

public sealed class PackVersionMainClass
{
    public string MainClass { get; set; } = "";
    public string Depends { get; set; } = "";
}

public sealed class PackVersionExtraArguments
{
    public string Arguments { get; set; } = "";
    public string Depends { get; set; } = "";
}

public sealed class PackVersion
{
    public string Version { get; set; } = "";
    public string Minecraft { get; set; } = "";
    public bool NoConfigs { get; set; }
    public PackVersionMainClass MainClass { get; } = new();
    public PackVersionExtraArguments ExtraArguments { get; } = new();

    public VersionLoader Loader { get; } = new();
    public List<VersionLibrary> Libraries { get; } = [];
    public List<VersionMod> Mods { get; } = [];
    public VersionConfigs Configs { get; } = new();

    public Dictionary<string, string> Colours { get; } = new();
    public Dictionary<string, string> Warnings { get; } = new();
    public VersionMessages Messages { get; } = new();

    public VersionKeeps Keeps { get; } = new();
    public VersionDeletes Deletes { get; } = new();
}

public static class AtLauncherPackManifest
{
    /// <summary>QJsonObject::contains(key) equivalent: true even when the member holds JSON null.</summary>
    private static bool Has(JsonNode? node, string key) => node is JsonObject map && map.ContainsKey(key);

    private static DownloadType ParseDownloadType(string rawType) => rawType switch
    {
        "server" => DownloadType.Server,
        "browser" => DownloadType.Browser,
        "direct" => DownloadType.Direct,
        _ => DownloadType.Unknown,
    };

    private static ModType ParseModType(string rawType) => rawType switch
    {
        // See https://wiki.atlauncher.com/mod_types
        "root" => ModType.Root,
        "forge" => ModType.Forge,
        "jar" => ModType.Jar,
        "mods" => ModType.Mods,
        "flan" => ModType.Flan,
        "dependency" or "depandency" => ModType.Dependency,
        "ic2lib" => ModType.Ic2Lib,
        "denlib" => ModType.DenLib,
        "coremods" => ModType.Coremods,
        "mcpc" => ModType.Mcpc,
        "plugins" => ModType.Plugins,
        "extract" => ModType.Extract,
        "decomp" => ModType.Decomp,
        "texturepack" => ModType.TexturePack,
        "resourcepack" => ModType.ResourcePack,
        "shaderpack" => ModType.ShaderPack,
        "texturepackextract" => ModType.TexturePackExtract,
        "resourcepackextract" => ModType.ResourcePackExtract,
        "millenaire" => ModType.Millenaire,
        _ => ModType.Unknown,
    };

    // ------------------------------------------------------------------ loaders

    private static void LoadVersionLoader(VersionLoader p, JsonNode obj)
    {
        p.Type = JsonUtils.RequireString(obj, "type", "'type'");
        p.Choose = JsonUtils.EnsureBoolean(obj["choose"]);

        var metadata = (JsonNode)JsonUtils.RequireObject(obj, "metadata", "'metadata'");
        p.Latest = JsonUtils.EnsureBoolean(metadata["latest"]);
        p.Recommended = JsonUtils.EnsureBoolean(metadata["recommended"]);

        // Minecraft Forge
        if (p.Type == "forge" || p.Type == "neoforge")
        {
            p.Version = JsonUtils.EnsureString(metadata["version"]);
        }

        // Fabric Loader
        if (p.Type == "fabric")
        {
            p.Version = JsonUtils.EnsureString(metadata["loader"]);
        }
    }

    private static void LoadVersionLibrary(VersionLibrary p, JsonNode obj)
    {
        p.Url = JsonUtils.RequireString(obj, "url", "'url'");
        p.File = JsonUtils.RequireString(obj, "file", "'file'");
        p.Md5 = JsonUtils.RequireString(obj, "md5", "'md5'");

        p.DownloadRaw = JsonUtils.RequireString(obj, "download", "'download'");
        p.Download = ParseDownloadType(p.DownloadRaw);

        p.Server = JsonUtils.EnsureString(obj["server"]);
    }

    private static void LoadVersionConfigs(VersionConfigs p, JsonNode obj)
    {
        p.Filesize = JsonUtils.EnsureInteger(obj["filesize"]);
        p.Sha1 = JsonUtils.RequireString(obj, "sha1", "'sha1'");
    }

    private static void LoadVersionMod(VersionMod p, JsonNode obj)
    {
        p.Name = JsonUtils.RequireString(obj, "name", "'name'");
        p.ModVersion = JsonUtils.RequireString(obj, "version", "'version'");
        p.Url = JsonUtils.RequireString(obj, "url", "'url'");
        p.File = JsonUtils.RequireString(obj, "file", "'file'");
        p.Md5 = JsonUtils.EnsureString(obj["md5"]);

        p.DownloadRaw = JsonUtils.RequireString(obj, "download", "'download'");
        p.Download = ParseDownloadType(p.DownloadRaw);

        p.TypeRaw = JsonUtils.RequireString(obj, "type", "'type'");
        p.Type = ParseModType(p.TypeRaw);

        // This contributes to the Minecraft Forge detection, where we rely on mod.type being "Forge"
        // when the mod represents Forge. As there is little difference between "Jar" and "Forge, some
        // packs regretfully use "Jar". This will correct the type to "Forge" in these cases (as best
        // it can).
        if (p.Name == "Minecraft Forge" && p.Type == ModType.Jar)
        {
            p.TypeRaw = "forge";
            p.Type = ModType.Forge;
        }

        if (Has(obj, "extractTo"))
        {
            p.ExtractToRaw = JsonUtils.RequireString(obj, "extractTo", "'extractTo'");
            p.ExtractTo = ParseModType(p.ExtractToRaw);
            p.ExtractFolder = JsonUtils.EnsureString(obj["extractFolder"]).Replace("%s%", "/");
        }

        if (Has(obj, "decompType"))
        {
            p.DecompTypeRaw = JsonUtils.RequireString(obj, "decompType", "'decompType'");
            p.DecompType = ParseModType(p.DecompTypeRaw);
            p.DecompFile = JsonUtils.RequireString(obj, "decompFile", "'decompFile'");
        }

        p.Description = JsonUtils.EnsureString(obj["description"]);
        p.Optional = JsonUtils.EnsureBoolean(obj["optional"]);
        p.Recommended = JsonUtils.EnsureBoolean(obj["recommended"]);
        p.Selected = JsonUtils.EnsureBoolean(obj["selected"]);
        p.Hidden = JsonUtils.EnsureBoolean(obj["hidden"]);
        p.Library = JsonUtils.EnsureBoolean(obj["library"]);
        p.Group = JsonUtils.EnsureString(obj["group"]);
        if (Has(obj, "depends"))
        {
            var dependsArr = JsonUtils.RequireArray(obj, "depends", "'depends'");
            foreach (var depends in dependsArr)
            {
                p.Depends.Add(JsonUtils.RequireString(depends));
            }
        }
        p.Colour = JsonUtils.EnsureString(obj["colour"]);
        p.Warning = JsonUtils.EnsureString(obj["warning"]);

        p.Client = JsonUtils.EnsureBoolean(obj["client"]);

        // computed
        p.EffectivelyHidden = p.Hidden || p.Library;
    }

    private static void LoadVersionMessages(VersionMessages m, JsonNode obj)
    {
        m.Install = JsonUtils.EnsureString(obj["install"]);
        m.Update = JsonUtils.EnsureString(obj["update"]);
    }

    private static void LoadVersionMainClass(PackVersionMainClass m, JsonNode obj)
    {
        m.MainClass = JsonUtils.EnsureString(obj["mainClass"]);
        m.Depends = JsonUtils.EnsureString(obj["depends"]);
    }

    private static void LoadVersionExtraArguments(PackVersionExtraArguments a, JsonNode obj)
    {
        a.Arguments = JsonUtils.EnsureString(obj["arguments"]);
        a.Depends = JsonUtils.EnsureString(obj["depends"]);
    }

    private static void LoadVersionKeep(VersionKeep k, JsonNode obj)
    {
        k.Base = JsonUtils.RequireString(obj, "base", "'base'");
        k.Target = JsonUtils.RequireString(obj, "target", "'target'");
    }

    private static void LoadVersionKeeps(VersionKeeps k, JsonNode obj)
    {
        if (Has(obj, "files"))
        {
            foreach (var keepRaw in JsonUtils.RequireArray(obj, "files", "'files'"))
            {
                var keep = new VersionKeep();
                LoadVersionKeep(keep, AtLauncherJson.Object(keepRaw));
                k.Files.Add(keep);
            }
        }

        if (Has(obj, "folders"))
        {
            foreach (var keepRaw in JsonUtils.RequireArray(obj, "folders", "'folders'"))
            {
                var keep = new VersionKeep();
                LoadVersionKeep(keep, AtLauncherJson.Object(keepRaw));
                k.Folders.Add(keep);
            }
        }
    }

    private static void LoadVersionDelete(VersionDelete d, JsonNode obj)
    {
        d.Base = JsonUtils.RequireString(obj, "base", "'base'");
        d.Target = JsonUtils.RequireString(obj, "target", "'target'");
    }

    private static void LoadVersionDeletes(VersionDeletes d, JsonNode obj)
    {
        if (Has(obj, "files"))
        {
            foreach (var deleteRaw in JsonUtils.RequireArray(obj, "files", "'files'"))
            {
                var versionDelete = new VersionDelete();
                LoadVersionDelete(versionDelete, AtLauncherJson.Object(deleteRaw));
                d.Files.Add(versionDelete);
            }
        }

        if (Has(obj, "folders"))
        {
            foreach (var deleteRaw in JsonUtils.RequireArray(obj, "folders", "'folders'"))
            {
                var versionDelete = new VersionDelete();
                LoadVersionDelete(versionDelete, AtLauncherJson.Object(deleteRaw));
                d.Folders.Add(versionDelete);
            }
        }
    }

    /// <summary>Port of ATLauncher::loadVersion.</summary>
    public static void LoadVersion(PackVersion v, JsonNode obj)
    {
        v.Version = JsonUtils.RequireString(obj, "version", "'version'");
        v.Minecraft = JsonUtils.RequireString(obj, "minecraft", "'minecraft'");
        v.NoConfigs = JsonUtils.EnsureBoolean(obj["noConfigs"]);

        if (Has(obj, "mainClass"))
        {
            LoadVersionMainClass(v.MainClass, JsonUtils.RequireObject(obj, "mainClass", "'mainClass'"));
        }

        if (Has(obj, "extraArguments"))
        {
            LoadVersionExtraArguments(v.ExtraArguments, JsonUtils.RequireObject(obj, "extraArguments", "'extraArguments'"));
        }

        if (Has(obj, "loader"))
        {
            LoadVersionLoader(v.Loader, JsonUtils.RequireObject(obj, "loader", "'loader'"));
        }

        if (Has(obj, "libraries"))
        {
            foreach (var libraryRaw in JsonUtils.RequireArray(obj, "libraries", "'libraries'"))
            {
                var target = new VersionLibrary();
                LoadVersionLibrary(target, AtLauncherJson.Object(libraryRaw));
                v.Libraries.Add(target);
            }
        }

        if (Has(obj, "mods"))
        {
            foreach (var modRaw in JsonUtils.RequireArray(obj, "mods", "'mods'"))
            {
                var mod = new VersionMod();
                LoadVersionMod(mod, AtLauncherJson.Object(modRaw));
                v.Mods.Add(mod);
            }
        }

        if (Has(obj, "configs"))
        {
            LoadVersionConfigs(v.Configs, JsonUtils.RequireObject(obj, "configs", "'configs'"));
        }

        if (obj["colours"] is JsonObject colourObj)
        {
            foreach (var (key, value) in colourObj)
            {
                v.Colours[key] = JsonUtils.EnsureString(value);
            }
        }

        if (obj["warnings"] is JsonObject warningObj)
        {
            foreach (var (key, value) in warningObj)
            {
                v.Warnings[key] = JsonUtils.EnsureString(value);
            }
        }

        if (obj["messages"] is JsonObject messages)
        {
            LoadVersionMessages(v.Messages, messages);
        }

        if (obj["keeps"] is JsonObject keeps)
        {
            LoadVersionKeeps(v.Keeps, keeps);
        }

        if (obj["deletes"] is JsonObject deletes)
        {
            LoadVersionDeletes(v.Deletes, deletes);
        }
    }
}
