// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022 Jamie Mansfield <jmansfield@cadixdev.org>
//
// This file incorporates work covered by the following copyright and
// permission notice:
//
//     Copyright 2020 Jamie Mansfield <jmansfield@cadixdev.org>
//     Copyright 2020-2021 Jamie Mansfield <jmansfield@cadixdev.org>
//     Copyright 2021 Petr Mrazek <peterix@gmail.com>
//
//     Licensed under the Apache License, Version 2.0 (the "License");
//     you may not use this file except in compliance with the License.
//     You may obtain a copy of the License at
//
//         http://www.apache.org/licenses/LICENSE-2.0
//
//     Unless required by applicable law or agreed to in writing, software
//     distributed under the License is distributed on an "AS IS" BASIS,
//     WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//     See the License for the specific language governing permissions and
//     limitations under the License.
//
// Port of modplatform/atlauncher/ATLPackManifest.{h,cpp}: the per-pack/version Configs.json
// model (loader, libraries, mods incl. optional flags, configs checksum, keeps/deletes,
// mainClass/extraArguments) with the exact fields the C++ loadVersion() reads. The wire
// shape is annotated on the model properties ([JsonPropertyName] + the QJsonValue-semantics
// annotations on the model properties); deserialization goes through the AtLauncherJsonContext
// (AtLauncherJson.cs).
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

using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlintLauncher.ExternalApi.AtLauncher;

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
    public required string Type { get; set; }

    public bool Choose { get; set; }

    public VersionLoaderMetadata Metadata { get; set; } = new();

    public bool Latest => Metadata.Latest;

    public bool Recommended => Metadata.Recommended;

    /// <summary>The Minecraft Forge/NeoForge members read metadata.version; Fabric reads
    /// metadata.loader (the key depends on the loader type, like the C++ loadVersion);
    /// null when that member is absent.</summary>
    public string? Version => Type switch
    {
        "forge" or "neoforge" => Metadata.Version,
        "fabric" => Metadata.Loader,
        _ => null,
    };
}

public sealed class VersionLoaderMetadata
{
    public bool Latest { get; set; }

    public bool Recommended { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("loader")]
    public string? Loader { get; set; }
}

public sealed class VersionLibrary
{
    public required string Url { get; set; }

    public required string File { get; set; }

    public string? Server { get; set; }

    public required string Md5 { get; set; }

    [JsonPropertyName("download")]
    public required string DownloadRaw { get; set; }

    [JsonIgnore]
    public DownloadType Download => AtLauncherPackManifest.ParseDownloadType(DownloadRaw);
}

public sealed class VersionMod
{
    public required string Name { get; set; }

    [JsonPropertyName("version")]
    public required string ModVersion { get; set; }

    public required string Url { get; set; }

    public required string File { get; set; }

    public string? Md5 { get; set; }

    [JsonPropertyName("download")]
    public required string DownloadRaw { get; set; }

    [JsonIgnore]
    public DownloadType Download => AtLauncherPackManifest.ParseDownloadType(DownloadRaw);

    [JsonPropertyName("type")]
    public required string TypeRaw { get; set; }

    [JsonIgnore]
    public ModType Type => AtLauncherPackManifest.ParseModType(TypeRaw);

    // absent extractTo/decompType keep the ModType.Root zero (the former property default),
    // not the Unknown mapping of an empty raw text
    [JsonPropertyName("extractTo")]
    public string? ExtractToRaw { get; set; }

    [JsonIgnore]
    public ModType ExtractTo => ExtractToRaw is null ? ModType.Root : AtLauncherPackManifest.ParseModType(ExtractToRaw);

    public string? ExtractFolder { get; set; }

    [JsonPropertyName("decompType")]
    public string? DecompTypeRaw { get; set; }

    [JsonIgnore]
    public ModType DecompType => DecompTypeRaw is null ? ModType.Root : AtLauncherPackManifest.ParseModType(DecompTypeRaw);

    public string? DecompFile { get; set; }

    public string? Description { get; set; }

    public bool Optional { get; set; }

    public bool Recommended { get; set; }

    public bool Selected { get; set; }

    public bool Hidden { get; set; }

    public bool Library { get; set; }

    public string? Group { get; set; }

    public List<string> Depends { get; set; } = [];

    public string? Colour { get; set; }

    public string? Warning { get; set; }

    public bool Client { get; set; }

    // computed
    public bool EffectivelyHidden => Hidden || Library;
}

public sealed class VersionConfigs
{
    public int Filesize { get; set; }

    public string? Sha1 { get; set; }
}

public sealed class VersionMessages
{
    public string? Install { get; set; }

    public string? Update { get; set; }
}

public sealed class VersionKeep
{
    public required string Base { get; set; }

    public required string Target { get; set; }
}

public sealed class VersionKeeps
{
    public List<VersionKeep> Files { get; set; } = [];

    public List<VersionKeep> Folders { get; set; } = [];
}

public sealed class VersionDelete
{
    public required string Base { get; set; }

    public required string Target { get; set; }
}

public sealed class VersionDeletes
{
    public List<VersionDelete> Files { get; set; } = [];

    public List<VersionDelete> Folders { get; set; } = [];
}

public sealed class PackVersionMainClass
{
    [JsonPropertyName("mainClass")]
    public string? MainClass { get; set; }

    public string? Depends { get; set; }
}

public sealed class PackVersionExtraArguments
{
    public string? Arguments { get; set; }

    public string? Depends { get; set; }
}

public sealed class PackVersion
{
    [JsonPropertyName("version")]
    public required string Version { get; set; }

    public required string Minecraft { get; set; }

    public bool NoConfigs { get; set; }

    [JsonPropertyName("mainClass")]
    public PackVersionMainClass? MainClass { get; init; }

    [JsonPropertyName("extraArguments")]
    public PackVersionExtraArguments? ExtraArguments { get; init; }

    [JsonPropertyName("loader")]
    public VersionLoader? Loader { get; init; }

    public List<VersionLibrary> Libraries { get; set; } = [];

    public List<VersionMod> Mods { get; set; } = [];

    [JsonPropertyName("configs")]
    public VersionConfigs? Configs { get; init; }

    public Dictionary<string, string> Colours { get; set; } = new();

    public Dictionary<string, string> Warnings { get; set; } = new();

    [JsonPropertyName("messages")]
    public VersionMessages? Messages { get; init; }

    [JsonPropertyName("keeps")]
    public VersionKeeps? Keeps { get; init; }

    [JsonPropertyName("deletes")]
    public VersionDeletes? Deletes { get; init; }
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

    /// <summary>The load-time fixups and the require checks of the former LoadVersionMod /
    /// LoadVersionLoader / LoadVersionConfigs: the "Minecraft Forge"-as-jar correction, the
    /// extractFolder %s% -> / rewrite, and the required loader.type / loader.metadata /
    /// configs.sha1 / decompFile members (a missing one fails the manifest instead of
    /// silently skipping the loader or installing configs without integrity checks).</summary>
    public static void Normalize(PackVersion version)
    {
        foreach (var mod in version.Mods)
        {
            if (mod is { Name: "Minecraft Forge", Type: ModType.Jar })
            {
                mod.TypeRaw = "forge";
            }

            if (mod.DecompTypeRaw is not null && mod.DecompFile is null)
            {
                throw new JsonException("'decompFile' is not a string");
            }

            if (mod.ExtractFolder is not null)
            {
                mod.ExtractFolder = mod.ExtractFolder.Replace("%s%", "/");
            }
        }

        if (version.Loader is { } loader && loader.Type.Length == 0)
        {
            throw new JsonException("'type' is not a string");
        }

        if (version.Configs is { Sha1: null } configs)
        {
            throw new JsonException("'sha1' is not a string");
        }
    }
}
