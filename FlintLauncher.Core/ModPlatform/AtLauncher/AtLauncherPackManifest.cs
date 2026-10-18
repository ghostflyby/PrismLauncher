// SPDX-License-Identifier: GPL-3.0-only
// Port of modplatform/atlauncher/ATLPackManifest.{h,cpp}: the per-pack/version Configs.json
// model (loader, libraries, mods incl. optional flags, configs checksum, keeps/deletes,
// mainClass/extraArguments) with the exact fields the C++ loadVersion() reads. The wire
// shape is annotated on the model properties ([JsonPropertyName] + the QJsonValue-semantics
// converters of QtConverters); deserialization goes through the AtLauncherJsonContext
// (AtLauncherApi.cs).
//
// Semantics notes:
//  - the former Has()-guarded members keep their two-sided behavior without nullable
//    probes: an absent key leaves the property default, a present-but-junky key fails the
//    strict binding (the raw enum texts stay in the *Raw members the loader used to fill);
//  - the enum members are computed views over the *Raw texts (ParseDownloadType /
//    ParseModType, including the "depandency" historical typo);
//  - the optional objects (mainClass/extraArguments/loader/configs/messages/keeps/deletes)
//    keep non-null surfaces through *Raw members with ?? new() computed views;
//  - the loader-time fixups (the "Minecraft Forge"-as-jar correction and the extractFolder
//    %s% -> / rewrite) run in Normalize(PackVersion) after deserialization.

using System.Text.Json.Serialization;
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
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("choose")]
    [JsonConverter(typeof(QtCoercingBoolConverter))]
    public bool Choose { get; set; }

    [JsonPropertyName("metadata")]
    public VersionLoaderMetadata Metadata { get; set; } = new();

    public bool Latest => Metadata.Latest;

    public bool Recommended => Metadata.Recommended;

    /// <summary>The Minecraft Forge/NeoForge members read metadata.version; Fabric reads
    /// metadata.loader (the key depends on the loader type, like the C++ loadVersion).</summary>
    public string Version => Type switch
    {
        "forge" or "neoforge" => Metadata.Version,
        "fabric" => Metadata.Loader,
        _ => "",
    };
}

public sealed class VersionLoaderMetadata
{
    [JsonPropertyName("latest")]
    [JsonConverter(typeof(QtCoercingBoolConverter))]
    public bool Latest { get; set; }

    [JsonPropertyName("recommended")]
    [JsonConverter(typeof(QtCoercingBoolConverter))]
    public bool Recommended { get; set; }

    [JsonPropertyName("version")]
    [JsonConverter(typeof(QtCoercingStringConverter))]
    public string Version { get; set; } = "";

    [JsonPropertyName("loader")]
    [JsonConverter(typeof(QtCoercingStringConverter))]
    public string Loader { get; set; } = "";
}

public sealed class VersionLibrary
{
    [JsonPropertyName("url")]
    public required string Url { get; set; }

    [JsonPropertyName("file")]
    public required string File { get; set; }

    [JsonPropertyName("server")]
    [JsonConverter(typeof(QtCoercingStringConverter))]
    public string Server { get; set; } = "";

    [JsonPropertyName("md5")]
    public required string Md5 { get; set; }

    [JsonPropertyName("download")]
    public required string DownloadRaw { get; set; }

    public DownloadType Download => AtLauncherPackManifest.ParseDownloadType(DownloadRaw);
}

public sealed class VersionMod
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("version")]
    public required string ModVersion { get; set; }

    [JsonPropertyName("url")]
    public required string Url { get; set; }

    [JsonPropertyName("file")]
    public required string File { get; set; }

    [JsonPropertyName("md5")]
    [JsonConverter(typeof(QtCoercingStringConverter))]
    public string Md5 { get; set; } = "";

    [JsonPropertyName("download")]
    public required string DownloadRaw { get; set; }

    public DownloadType Download => AtLauncherPackManifest.ParseDownloadType(DownloadRaw);

    [JsonPropertyName("type")]
    public required string TypeRaw { get; set; }

    public ModType Type => AtLauncherPackManifest.ParseModType(TypeRaw);

    // absent extractTo/decompType keep the ModType.Root zero (the former property default),
    // not the Unknown mapping of an empty raw text
    [JsonPropertyName("extractTo")]
    [JsonConverter(typeof(QtCoercingStringConverter))]
    public string ExtractToRaw { get; set; } = "";

    public ModType ExtractTo => ExtractToRaw.Length == 0 ? ModType.Root : AtLauncherPackManifest.ParseModType(ExtractToRaw);

    [JsonPropertyName("extractFolder")]
    [JsonConverter(typeof(QtCoercingStringConverter))]
    public string ExtractFolder { get; set; } = "";

    [JsonPropertyName("decompType")]
    [JsonConverter(typeof(QtCoercingStringConverter))]
    public string DecompTypeRaw { get; set; } = "";

    public ModType DecompType => DecompTypeRaw.Length == 0 ? ModType.Root : AtLauncherPackManifest.ParseModType(DecompTypeRaw);

    [JsonPropertyName("decompFile")]
    [JsonConverter(typeof(QtCoercingStringConverter))]
    public string DecompFile { get; set; } = "";

    [JsonPropertyName("description")]
    [JsonConverter(typeof(QtCoercingStringConverter))]
    public string Description { get; set; } = "";

    [JsonPropertyName("optional")]
    [JsonConverter(typeof(QtCoercingBoolConverter))]
    public bool Optional { get; set; }

    [JsonPropertyName("recommended")]
    [JsonConverter(typeof(QtCoercingBoolConverter))]
    public bool Recommended { get; set; }

    [JsonPropertyName("selected")]
    [JsonConverter(typeof(QtCoercingBoolConverter))]
    public bool Selected { get; set; }

    [JsonPropertyName("hidden")]
    [JsonConverter(typeof(QtCoercingBoolConverter))]
    public bool Hidden { get; set; }

    [JsonPropertyName("library")]
    [JsonConverter(typeof(QtCoercingBoolConverter))]
    public bool Library { get; set; }

    [JsonPropertyName("group")]
    [JsonConverter(typeof(QtCoercingStringConverter))]
    public string Group { get; set; } = "";

    [JsonPropertyName("depends")]
    public List<string> Depends { get; set; } = [];

    [JsonPropertyName("colour")]
    [JsonConverter(typeof(QtCoercingStringConverter))]
    public string Colour { get; set; } = "";

    [JsonPropertyName("warning")]
    [JsonConverter(typeof(QtCoercingStringConverter))]
    public string Warning { get; set; } = "";

    [JsonPropertyName("client")]
    [JsonConverter(typeof(QtCoercingBoolConverter))]
    public bool Client { get; set; }

    // computed
    public bool EffectivelyHidden => Hidden || Library;
}

public sealed class VersionConfigs
{
    [JsonPropertyName("filesize")]
    [JsonConverter(typeof(QtCoercingIntConverter))]
    public int Filesize { get; set; }

    [JsonPropertyName("sha1")]
    [JsonConverter(typeof(QtCoercingStringConverter))]
    public string Sha1 { get; set; } = "";
}

public sealed class VersionMessages
{
    [JsonPropertyName("install")]
    [JsonConverter(typeof(QtCoercingStringConverter))]
    public string Install { get; set; } = "";

    [JsonPropertyName("update")]
    [JsonConverter(typeof(QtCoercingStringConverter))]
    public string Update { get; set; } = "";
}

public sealed class VersionKeep
{
    [JsonPropertyName("base")]
    public required string Base { get; set; }

    [JsonPropertyName("target")]
    public required string Target { get; set; }
}

public sealed class VersionKeeps
{
    [JsonPropertyName("files")]
    public List<VersionKeep> Files { get; set; } = [];

    [JsonPropertyName("folders")]
    public List<VersionKeep> Folders { get; set; } = [];
}

public sealed class VersionDelete
{
    [JsonPropertyName("base")]
    public required string Base { get; set; }

    [JsonPropertyName("target")]
    public required string Target { get; set; }
}

public sealed class VersionDeletes
{
    [JsonPropertyName("files")]
    public List<VersionDelete> Files { get; set; } = [];

    [JsonPropertyName("folders")]
    public List<VersionDelete> Folders { get; set; } = [];
}

public sealed class PackVersionMainClass
{
    [JsonPropertyName("mainClass")]
    [JsonConverter(typeof(QtCoercingStringConverter))]
    public string MainClass { get; set; } = "";

    [JsonPropertyName("depends")]
    [JsonConverter(typeof(QtCoercingStringConverter))]
    public string Depends { get; set; } = "";
}

public sealed class PackVersionExtraArguments
{
    [JsonPropertyName("arguments")]
    [JsonConverter(typeof(QtCoercingStringConverter))]
    public string Arguments { get; set; } = "";

    [JsonPropertyName("depends")]
    [JsonConverter(typeof(QtCoercingStringConverter))]
    public string Depends { get; set; } = "";
}

public sealed class PackVersion
{
    [JsonPropertyName("version")]
    public required string Version { get; set; }

    [JsonPropertyName("minecraft")]
    public required string Minecraft { get; set; }

    [JsonPropertyName("noConfigs")]
    [JsonConverter(typeof(QtCoercingBoolConverter))]
    public bool NoConfigs { get; set; }

    [JsonPropertyName("mainClass")]
    public PackVersionMainClass? MainClassRaw { get; init; }

    public PackVersionMainClass MainClass => MainClassRaw ?? new();

    [JsonPropertyName("extraArguments")]
    public PackVersionExtraArguments? ExtraArgumentsRaw { get; init; }

    public PackVersionExtraArguments ExtraArguments => ExtraArgumentsRaw ?? new();

    [JsonPropertyName("loader")]
    public VersionLoader? LoaderRaw { get; init; }

    public VersionLoader Loader => LoaderRaw ?? new();

    [JsonPropertyName("libraries")]
    public List<VersionLibrary> Libraries { get; set; } = [];

    [JsonPropertyName("mods")]
    public List<VersionMod> Mods { get; set; } = [];

    [JsonPropertyName("configs")]
    public VersionConfigs? ConfigsRaw { get; init; }

    public VersionConfigs Configs => ConfigsRaw ?? new();

    [JsonPropertyName("colours")]
    public Dictionary<string, string> Colours { get; set; } = new();

    [JsonPropertyName("warnings")]
    public Dictionary<string, string> Warnings { get; set; } = new();

    [JsonPropertyName("messages")]
    public VersionMessages? MessagesRaw { get; init; }

    public VersionMessages Messages => MessagesRaw ?? new();

    [JsonPropertyName("keeps")]
    public VersionKeeps? KeepsRaw { get; init; }

    public VersionKeeps Keeps => KeepsRaw ?? new();

    [JsonPropertyName("deletes")]
    public VersionDeletes? DeletesRaw { get; init; }

    public VersionDeletes Deletes => DeletesRaw ?? new();
}

public static class AtLauncherPackManifest
{
    internal static DownloadType ParseDownloadType(string rawType) => rawType switch
    {
        "server" => DownloadType.Server,
        "browser" => DownloadType.Browser,
        "direct" => DownloadType.Direct,
        _ => DownloadType.Unknown,
    };

    internal static ModType ParseModType(string rawType) => rawType switch
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

    /// <summary>The load-time fixups of the former LoadVersionMod: the "Minecraft
    /// Forge"-as-jar correction (the Forge detection relies on mod.type being Forge, and
    /// some packs regretfully use "jar") and the extractFolder %s% -> / rewrite.</summary>
    public static void Normalize(PackVersion version)
    {
        foreach (var mod in version.Mods)
        {
            if (mod is { Name: "Minecraft Forge", Type: ModType.Jar })
            {
                mod.TypeRaw = "forge";
            }

            mod.ExtractFolder = mod.ExtractFolder.Replace("%s%", "/");
        }
    }
}
