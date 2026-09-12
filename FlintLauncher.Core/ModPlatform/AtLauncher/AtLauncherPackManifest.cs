// SPDX-License-Identifier: GPL-3.0-only
// Port of modplatform/atlauncher/ATLPackManifest.{h,cpp}: the per-pack/version Configs.json
// model (loader, libraries, mods incl. optional flags, configs checksum, keeps/deletes,
// mainClass/extraArguments) with the exact fields the C++ loadVersion() reads.
using System.Text.Json.Nodes;

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
    MCPC,
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
        "mcpc" => ModType.MCPC,
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

    private static void LoadVersionLoader(VersionLoader p, JsonObject obj)
    {
        p.Type = AtLauncherJson.RequireString(obj, "type");
        p.Choose = AtLauncherJson.OptBool(obj, "choose");

        var metadata = AtLauncherJson.RequireObject(obj, "metadata");
        p.Latest = AtLauncherJson.OptBool(metadata, "latest");
        p.Recommended = AtLauncherJson.OptBool(metadata, "recommended");

        // Minecraft Forge
        if (p.Type == "forge" || p.Type == "neoforge")
        {
            p.Version = AtLauncherJson.OptString(metadata, "version");
        }

        // Fabric Loader
        if (p.Type == "fabric")
        {
            p.Version = AtLauncherJson.OptString(metadata, "loader");
        }
    }

    private static void LoadVersionLibrary(VersionLibrary p, JsonObject obj)
    {
        p.Url = AtLauncherJson.RequireString(obj, "url");
        p.File = AtLauncherJson.RequireString(obj, "file");
        p.Md5 = AtLauncherJson.RequireString(obj, "md5");

        p.DownloadRaw = AtLauncherJson.RequireString(obj, "download");
        p.Download = ParseDownloadType(p.DownloadRaw);

        p.Server = AtLauncherJson.OptString(obj, "server");
    }

    private static void LoadVersionConfigs(VersionConfigs p, JsonObject obj)
    {
        p.Filesize = AtLauncherJson.OptInteger(obj, "filesize");
        p.Sha1 = AtLauncherJson.RequireString(obj, "sha1");
    }

    private static void LoadVersionMod(VersionMod p, JsonObject obj)
    {
        p.Name = AtLauncherJson.RequireString(obj, "name");
        p.ModVersion = AtLauncherJson.RequireString(obj, "version");
        p.Url = AtLauncherJson.RequireString(obj, "url");
        p.File = AtLauncherJson.RequireString(obj, "file");
        p.Md5 = AtLauncherJson.OptString(obj, "md5");

        p.DownloadRaw = AtLauncherJson.RequireString(obj, "download");
        p.Download = ParseDownloadType(p.DownloadRaw);

        p.TypeRaw = AtLauncherJson.RequireString(obj, "type");
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

        if (obj.ContainsKey("extractTo"))
        {
            p.ExtractToRaw = AtLauncherJson.RequireString(obj, "extractTo");
            p.ExtractTo = ParseModType(p.ExtractToRaw);
            p.ExtractFolder = AtLauncherJson.OptString(obj, "extractFolder").Replace("%s%", "/");
        }

        if (obj.ContainsKey("decompType"))
        {
            p.DecompTypeRaw = AtLauncherJson.RequireString(obj, "decompType");
            p.DecompType = ParseModType(p.DecompTypeRaw);
            p.DecompFile = AtLauncherJson.RequireString(obj, "decompFile");
        }

        p.Description = AtLauncherJson.OptString(obj, "description");
        p.Optional = AtLauncherJson.OptBool(obj, "optional");
        p.Recommended = AtLauncherJson.OptBool(obj, "recommended");
        p.Selected = AtLauncherJson.OptBool(obj, "selected");
        p.Hidden = AtLauncherJson.OptBool(obj, "hidden");
        p.Library = AtLauncherJson.OptBool(obj, "library");
        p.Group = AtLauncherJson.OptString(obj, "group");
        if (obj.ContainsKey("depends"))
        {
            var dependsArr = AtLauncherJson.RequireArray(obj, "depends");
            foreach (var depends in dependsArr)
            {
                p.Depends.Add(AtLauncherJson.RequireString(depends));
            }
        }
        p.Colour = AtLauncherJson.OptString(obj, "colour");
        p.Warning = AtLauncherJson.OptString(obj, "warning");

        p.Client = AtLauncherJson.OptBool(obj, "client");

        // computed
        p.EffectivelyHidden = p.Hidden || p.Library;
    }

    private static void LoadVersionMessages(VersionMessages m, JsonObject obj)
    {
        m.Install = AtLauncherJson.OptString(obj, "install");
        m.Update = AtLauncherJson.OptString(obj, "update");
    }

    private static void LoadVersionMainClass(PackVersionMainClass m, JsonObject obj)
    {
        m.MainClass = AtLauncherJson.OptString(obj, "mainClass");
        m.Depends = AtLauncherJson.OptString(obj, "depends");
    }

    private static void LoadVersionExtraArguments(PackVersionExtraArguments a, JsonObject obj)
    {
        a.Arguments = AtLauncherJson.OptString(obj, "arguments");
        a.Depends = AtLauncherJson.OptString(obj, "depends");
    }

    private static void LoadVersionKeep(VersionKeep k, JsonObject obj)
    {
        k.Base = AtLauncherJson.RequireString(obj, "base");
        k.Target = AtLauncherJson.RequireString(obj, "target");
    }

    private static void LoadVersionKeeps(VersionKeeps k, JsonObject obj)
    {
        if (obj.ContainsKey("files"))
        {
            foreach (var keepRaw in AtLauncherJson.RequireArray(obj, "files"))
            {
                var keep = new VersionKeep();
                LoadVersionKeep(keep, AtLauncherJson.Object(keepRaw));
                k.Files.Add(keep);
            }
        }

        if (obj.ContainsKey("folders"))
        {
            foreach (var keepRaw in AtLauncherJson.RequireArray(obj, "folders"))
            {
                var keep = new VersionKeep();
                LoadVersionKeep(keep, AtLauncherJson.Object(keepRaw));
                k.Folders.Add(keep);
            }
        }
    }

    private static void LoadVersionDelete(VersionDelete d, JsonObject obj)
    {
        d.Base = AtLauncherJson.RequireString(obj, "base");
        d.Target = AtLauncherJson.RequireString(obj, "target");
    }

    private static void LoadVersionDeletes(VersionDeletes d, JsonObject obj)
    {
        if (obj.ContainsKey("files"))
        {
            foreach (var deleteRaw in AtLauncherJson.RequireArray(obj, "files"))
            {
                var versionDelete = new VersionDelete();
                LoadVersionDelete(versionDelete, AtLauncherJson.Object(deleteRaw));
                d.Files.Add(versionDelete);
            }
        }

        if (obj.ContainsKey("folders"))
        {
            foreach (var deleteRaw in AtLauncherJson.RequireArray(obj, "folders"))
            {
                var versionDelete = new VersionDelete();
                LoadVersionDelete(versionDelete, AtLauncherJson.Object(deleteRaw));
                d.Folders.Add(versionDelete);
            }
        }
    }

    /// <summary>Port of ATLauncher::loadVersion.</summary>
    public static void LoadVersion(PackVersion v, JsonObject obj)
    {
        v.Version = AtLauncherJson.RequireString(obj, "version");
        v.Minecraft = AtLauncherJson.RequireString(obj, "minecraft");
        v.NoConfigs = AtLauncherJson.OptBool(obj, "noConfigs");

        if (obj.ContainsKey("mainClass"))
        {
            LoadVersionMainClass(v.MainClass, AtLauncherJson.RequireObject(obj, "mainClass"));
        }

        if (obj.ContainsKey("extraArguments"))
        {
            LoadVersionExtraArguments(v.ExtraArguments, AtLauncherJson.RequireObject(obj, "extraArguments"));
        }

        if (obj.ContainsKey("loader"))
        {
            LoadVersionLoader(v.Loader, AtLauncherJson.RequireObject(obj, "loader"));
        }

        if (obj.ContainsKey("libraries"))
        {
            foreach (var libraryRaw in AtLauncherJson.RequireArray(obj, "libraries"))
            {
                var target = new VersionLibrary();
                LoadVersionLibrary(target, AtLauncherJson.Object(libraryRaw));
                v.Libraries.Add(target);
            }
        }

        if (obj.ContainsKey("mods"))
        {
            foreach (var modRaw in AtLauncherJson.RequireArray(obj, "mods"))
            {
                var mod = new VersionMod();
                LoadVersionMod(mod, AtLauncherJson.Object(modRaw));
                v.Mods.Add(mod);
            }
        }

        if (obj.ContainsKey("configs"))
        {
            LoadVersionConfigs(v.Configs, AtLauncherJson.RequireObject(obj, "configs"));
        }

        if (obj.TryGetPropertyValue("colours", out var coloursNode) && coloursNode is JsonObject colourObj)
        {
            foreach (var (key, value) in colourObj)
            {
                v.Colours[key] = AtLauncherJson.OptString(value);
            }
        }

        if (obj.TryGetPropertyValue("warnings", out var warningsNode) && warningsNode is JsonObject warningObj)
        {
            foreach (var (key, value) in warningObj)
            {
                v.Warnings[key] = AtLauncherJson.OptString(value);
            }
        }

        if (obj.TryGetPropertyValue("messages", out var messagesNode) && messagesNode is JsonObject messages)
        {
            LoadVersionMessages(v.Messages, messages);
        }

        if (obj.TryGetPropertyValue("keeps", out var keepsNode) && keepsNode is JsonObject keeps)
        {
            LoadVersionKeeps(v.Keeps, keeps);
        }

        if (obj.TryGetPropertyValue("deletes", out var deletesNode) && deletesNode is JsonObject deletes)
        {
            LoadVersionDeletes(v.Deletes, deletes);
        }
    }
}
