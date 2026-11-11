// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022 Sefa Eyeoglu <contact@scrumplex.net>
//
// This file incorporates work covered by the following copyright and
// permission notice:
//
//     Copyright 2020-2021 Jamie Mansfield <jmansfield@cadixdev.org>
//     Copyright 2020 Petr Mrazek <peterix@gmail.com>
//     Copyright 2020 Jamie Mansfield <jmansfield@cadixdev.org>
//     Copyright 2020-2021 Petr Mrazek <peterix@gmail.com>
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
// The modpacks.ch-style JSON models of the FTB App API — the pack description (Modpack
// with art/authors/versions/tags) and the version payload (Version with targets[] and
// files[]). The wire shape is annotated directly on the model properties
// ([JsonPropertyName]); required members use the
// C# `required` modifier so a missing key fails deserialization, and the context turns
// them into source-generated metadata with RespectNullableAnnotations (a JSON null for a
// required member is an error). Optional members stay nullable; Modpack.SafeName is a
// computed property instead of a parsed field.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;

namespace FlintLauncher.ExternalApi.Ftb;

/// <summary>The /modpack/all wire root: the pack ids as the answer lists them.</summary>
public sealed class FtbPackIndexFile
{
    public List<int> Packs { get; init; } = [];
}

/// <summary>Port of FTB::Specs.</summary>
public sealed class FtbSpecs
{
    public required int Id { get; set; }

    public required int Minimum { get; set; }

    public required int Recommended { get; set; }
}

/// <summary>Port of FTB::Tag.</summary>
public sealed class FtbTag
{
    public required int Id { get; set; }

    public required string Name { get; set; }
}

/// <summary>Port of FTB::Art.</summary>
public sealed class FtbArt
{
    public required int Id { get; set; }

    public required string Url { get; set; }

    public required string Type { get; set; }

    public required int Width { get; set; }

    public required int Height { get; set; }

    public required bool Compressed { get; set; }

    public required string Sha1 { get; set; }

    // absent on the live art payloads
    public int? Size { get; set; }

    public required long Updated { get; set; }
}

/// <summary>Port of FTB::Author.</summary>
public sealed class FtbAuthor
{
    public required int Id { get; set; }

    public required string Name { get; set; }

    public required string Type { get; set; }

    public required string Website { get; set; }

    public required long Updated { get; set; }
}

/// <summary>Port of FTB::VersionInfo.</summary>
public sealed class FtbVersionInfo
{
    public required int Id { get; set; }

    public required string Name { get; set; }

    public required string Type { get; set; }

    public required long Updated { get; set; }

    public required FtbSpecs Specs { get; init; }
}

/// <summary>Port of FTB::Modpack.</summary>
public sealed partial class FtbModpack
{
    public required int Id { get; set; }

    public required string Name { get; set; }

    public required string Synopsis { get; set; }

    public required string Description { get; set; }

    public required string Type { get; set; }

    public required bool Featured { get; set; }

    public required int Installs { get; set; }

    public required int Plays { get; set; }

    public required long Updated { get; set; }

    // absent on the live pack payloads
    public long? Refreshed { get; set; }

    public required List<FtbArt> Art { get; init; }

    public required List<FtbAuthor> Authors { get; init; }

    public required List<FtbVersionInfo> Versions { get; init; }

    public required List<FtbTag> Tags { get; init; }

    /// <summary>Port of the safeName computed at parse time:
    /// name with every non-alphanumeric removed, lowercased + ".png".</summary>
    [JsonIgnore]
    public string SafeName => SafeNameOf(Name);

    public static string SafeNameOf(string name) =>
        NonAlphanumericRegex().Replace(name, "").ToLowerInvariant() + ".png";

    [GeneratedRegex("[^A-Za-z0-9]")]
    private static partial Regex NonAlphanumericRegex();
}

/// <summary>Port of FTB::VersionTarget.</summary>
public sealed class FtbVersionTarget
{
    public required int Id { get; set; }

    public required string Type { get; set; }

    public required string Name { get; set; }

    public required string Version { get; set; }

    public required long Updated { get; set; }
}

/// <summary>Port of FTB::VersionFileCurseForge.</summary>
public sealed class FtbVersionFileCurseForge
{
    [JsonPropertyName("project")]
    public required int ProjectId { get; set; }

    [JsonPropertyName("file")]
    public required int FileId { get; set; }
}

/// <summary>Port of FTB::VersionFile.</summary>
public sealed class FtbVersionFile
{
    public required int Id { get; set; }

    public required string Type { get; set; }

    public required string Path { get; set; }

    public required string Name { get; set; }

    public required string Version { get; set; }

    // CurseForge-hosted files carry no direct url
    public string? Url { get; set; }

    public required string Sha1 { get; set; }

    public int? Size { get; set; }

    [JsonPropertyName("clientonly")]
    public required bool ClientOnly { get; set; }

    [JsonPropertyName("serveronly")]
    public required bool ServerOnly { get; set; }

    public required bool Optional { get; set; }

    public required long Updated { get; set; }

    // absent unless the file is CurseForge-resolved
    [JsonPropertyName("curseforge")]
    public FtbVersionFileCurseForge? CurseforgeRaw { get; init; }
}

/// <summary>Port of FTB::Version.</summary>
public sealed class FtbVersion
{
    public required int Id { get; set; }

    public required int Parent { get; set; }

    public required string Name { get; set; }

    public required string Type { get; set; }

    public required int Installs { get; set; }

    public required int Plays { get; set; }

    public required long Updated { get; set; }

    // absent on the live version payloads
    public long? Refreshed { get; set; }

    public required FtbSpecs Specs { get; init; }

    public required List<FtbVersionTarget> Targets { get; init; }

    public required List<FtbVersionFile> Files { get; init; }
}

[JsonSourceGenerationOptions(RespectNullableAnnotations = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FtbPackIndexFile))]
[JsonSerializable(typeof(FtbModpack))]
[JsonSerializable(typeof(FtbVersion))]
public sealed partial class FtbJsonContext : JsonSerializerContext;

/// <summary>The strict document decode of the FTB payloads: parse with the
/// "JSON response from FTB: Error parsing JSON: ..." wrapper, guard the object root, then
/// bind the annotated model — the entry points the operations read responses through and
/// callers reuse for out-of-band payloads.</summary>
public static class FtbJson
{
    /// <summary>Parses the /modpack/all index: {"packs":[id,...]} -> the pack ids. A
    /// missing/invalid payload yields an empty list.</summary>
    public static List<int> LoadPackIndex(string response)
    {
        try
        {
            return JsonSerializer.Deserialize(response, FtbJsonContext.Default.FtbPackIndexFile)?.Packs ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Parses a /modpack/{id} response; binding failures (a missing required key or
    /// a malformed member) ride the parse wrapper.</summary>
    public static FtbModpack LoadModpack(string response) =>
        LoadRootDocument(response, FtbJsonContext.Default.FtbModpack);

    /// <summary>Parses a /modpack/{id}/{versionId} response the same way.</summary>
    public static FtbVersion LoadVersion(string response) =>
        LoadRootDocument(response, FtbJsonContext.Default.FtbVersion);

    /// <summary>The shared document binding: deserialize the annotated model, failures
    /// carrying the parse wrapper (a "null" document binds to null and fails the same
    /// way).</summary>
    private static T LoadRootDocument<T>(string json, JsonTypeInfo<T> typeInfo) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(json, typeInfo)
                   ?? throw new JsonException("Document is not an object");
        }
        catch (JsonException e)
        {
            throw new JsonException("JSON response from FTB: Error parsing JSON: " + e.Message, e);
        }
    }
}
