// SPDX-License-Identifier: GPL-3.0-only
// Port of modplatform/ftb/FTBPackManifest.{h,cpp}: the modpacks.ch-style JSON models of the
// FTB App API — the pack description (Modpack with art/authors/versions/tags) and the
// version payload (Version with targets[] and files[]). The wire shape is annotated directly
// on the model properties ([JsonPropertyName] + the QJsonValue-semantics converters of
// QtConverters); required members use the C# `required` modifier so a missing key fails
// deserialization, and FtbJsonContext (FtbApi.cs) turns them into source-generated
// deserialization metadata with RespectNullableAnnotations (a JSON null for a required
// member is an error, like the former requireIsType reads).
//
// Deviations: optional fields keep the QJsonValue coercion defaults through the coercing
// converters (toInt() of a missing value -> 0, toString() -> ""); Modpack.safeName is a
// computed property instead of a parsed field.

using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FlintLauncher.Core.Json;

namespace FlintLauncher.Core.ModPlatform.Ftb;

/// <summary>Port of FTB::Specs.</summary>
public sealed class FtbSpecs
{
    [JsonPropertyName("id")]
    public required int Id { get; set; }

    [JsonPropertyName("minimum")]
    public required int Minimum { get; set; }

    [JsonPropertyName("recommended")]
    public required int Recommended { get; set; }
}

/// <summary>Port of FTB::Tag.</summary>
public sealed class FtbTag
{
    [JsonPropertyName("id")]
    public required int Id { get; set; }

    [JsonPropertyName("name")]
    public required string Name { get; set; }
}

/// <summary>Port of FTB::Art.</summary>
public sealed class FtbArt
{
    [JsonPropertyName("id")]
    public required int Id { get; set; }

    [JsonPropertyName("url")]
    public required string Url { get; set; }

    [JsonPropertyName("type")]
    public required string Type { get; set; }

    [JsonPropertyName("width")]
    public required int Width { get; set; }

    [JsonPropertyName("height")]
    public required int Height { get; set; }

    [JsonPropertyName("compressed")]
    public required bool Compressed { get; set; }

    [JsonPropertyName("sha1")]
    public required string Sha1 { get; set; }

    // optional: toInt() of a missing/junk value -> 0
    [JsonPropertyName("size")]
    [JsonConverter(typeof(QtCoercingIntConverter))]
    public int Size { get; set; }

    [JsonPropertyName("updated")]
    public required long Updated { get; set; }
}

/// <summary>Port of FTB::Author.</summary>
public sealed class FtbAuthor
{
    [JsonPropertyName("id")]
    public required int Id { get; set; }

    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("type")]
    public required string Type { get; set; }

    [JsonPropertyName("website")]
    public required string Website { get; set; }

    [JsonPropertyName("updated")]
    public required long Updated { get; set; }
}

/// <summary>Port of FTB::VersionInfo.</summary>
public sealed class FtbVersionInfo
{
    [JsonPropertyName("id")]
    public required int Id { get; set; }

    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("type")]
    public required string Type { get; set; }

    [JsonPropertyName("updated")]
    public required long Updated { get; set; }

    [JsonPropertyName("specs")]
    public required FtbSpecs Specs { get; init; }
}

/// <summary>Port of FTB::Modpack.</summary>
public sealed partial class FtbModpack
{
    [JsonPropertyName("id")]
    public required int Id { get; set; }

    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("synopsis")]
    public required string Synopsis { get; set; }

    [JsonPropertyName("description")]
    public required string Description { get; set; }

    [JsonPropertyName("type")]
    public required string Type { get; set; }

    [JsonPropertyName("featured")]
    public required bool Featured { get; set; }

    [JsonPropertyName("installs")]
    public required int Installs { get; set; }

    [JsonPropertyName("plays")]
    public required int Plays { get; set; }

    [JsonPropertyName("updated")]
    public required long Updated { get; set; }

    // optional: toInt() truncates a fractional double (20.7 -> 20)
    [JsonPropertyName("refreshed")]
    [JsonConverter(typeof(QtCoercingLongConverter))]
    public long Refreshed { get; set; }

    [JsonPropertyName("art")]
    public required List<FtbArt> Art { get; init; }

    [JsonPropertyName("authors")]
    public required List<FtbAuthor> Authors { get; init; }

    [JsonPropertyName("versions")]
    public required List<FtbVersionInfo> Versions { get; init; }

    [JsonPropertyName("tags")]
    public required List<FtbTag> Tags { get; init; }

    /// <summary>Port of the safeName computed at parse time:
    /// name with every non-alphanumeric removed, lowercased + ".png".</summary>
    public string SafeName => SafeNameOf(Name);

    public static string SafeNameOf(string name) =>
        NonAlphanumericRegex().Replace(name, "").ToLowerInvariant() + ".png";

    [GeneratedRegex("[^A-Za-z0-9]")]
    private static partial Regex NonAlphanumericRegex();
}

/// <summary>Port of FTB::VersionTarget.</summary>
public sealed class FtbVersionTarget
{
    [JsonPropertyName("id")]
    public required int Id { get; set; }

    [JsonPropertyName("type")]
    public required string Type { get; set; }

    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("version")]
    public required string Version { get; set; }

    [JsonPropertyName("updated")]
    public required long Updated { get; set; }
}

/// <summary>Port of FTB::VersionFileCurseForge.</summary>
public sealed class FtbVersionFileCurseForge
{
    // optional members of an already-optional object: toInt() of a missing/junk value -> 0
    [JsonPropertyName("project")]
    [JsonConverter(typeof(QtCoercingIntConverter))]
    public int ProjectId { get; set; }

    [JsonPropertyName("file")]
    [JsonConverter(typeof(QtCoercingIntConverter))]
    public int FileId { get; set; }
}

/// <summary>Port of FTB::VersionFile.</summary>
public sealed class FtbVersionFile
{
    [JsonPropertyName("id")]
    public required int Id { get; set; }

    [JsonPropertyName("type")]
    public required string Type { get; set; }

    [JsonPropertyName("path")]
    public required string Path { get; set; }

    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("version")]
    public required string Version { get; set; }

    // optional: toString() of a missing/junk value -> ""
    [JsonPropertyName("url")]
    [JsonConverter(typeof(QtCoercingStringConverter))]
    public string Url { get; set; } = "";

    [JsonPropertyName("sha1")]
    public required string Sha1 { get; set; }

    // optional: toInt() of a missing/junk value -> 0
    [JsonPropertyName("size")]
    [JsonConverter(typeof(QtCoercingIntConverter))]
    public int Size { get; set; }

    [JsonPropertyName("clientonly")]
    public required bool ClientOnly { get; set; }

    [JsonPropertyName("serveronly")]
    public required bool ServerOnly { get; set; }

    [JsonPropertyName("optional")]
    public required bool Optional { get; set; }

    [JsonPropertyName("updated")]
    public required long Updated { get; set; }

    // the optional curseforge object: a missing (or JSON null) member keeps the zero default
    [JsonPropertyName("curseforge")]
    public FtbVersionFileCurseForge? CurseforgeRaw { get; init; }

    public FtbVersionFileCurseForge Curseforge => CurseforgeRaw ?? new();
}

/// <summary>Port of FTB::Version.</summary>
public sealed class FtbVersion
{
    [JsonPropertyName("id")]
    public required int Id { get; set; }

    [JsonPropertyName("parent")]
    public required int Parent { get; set; }

    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("type")]
    public required string Type { get; set; }

    [JsonPropertyName("installs")]
    public required int Installs { get; set; }

    [JsonPropertyName("plays")]
    public required int Plays { get; set; }

    [JsonPropertyName("updated")]
    public required long Updated { get; set; }

    // optional: toInt() truncates a fractional double (20.7 -> 20)
    [JsonPropertyName("refreshed")]
    [JsonConverter(typeof(QtCoercingLongConverter))]
    public long Refreshed { get; set; }

    [JsonPropertyName("specs")]
    public required FtbSpecs Specs { get; init; }

    [JsonPropertyName("targets")]
    public required List<FtbVersionTarget> Targets { get; init; }

    [JsonPropertyName("files")]
    public required List<FtbVersionFile> Files { get; init; }
}
