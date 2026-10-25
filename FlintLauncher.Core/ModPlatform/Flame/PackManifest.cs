// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022 Sefa Eyeoglu <contact@scrumplex.net>
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
//
// This file incorporates work covered by the following copyright and
// permission notice:
//
//     Copyright 2013-2021 MultiMC Contributors
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
// Port of modplatform/flame/PackManifest.{h,cpp}: the CurseForge modpack manifest.json
// model (manifestType "minecraftModpack", manifestVersion 1) and its loader. The C++
// QMap<int, File> (key-sorted) becomes a SortedDictionary keyed by file id.
//
// The wire shape arrives as the FlameManifestJson probe records (deserialized through the
// FlameJsonContext partial that lives in this file); the loader validates the required
// members with the Json::require* messages and maps into the domain model, keeping the
// QJsonValue optional defaults (name "Unnamed", author "Anonymous", overrides "overrides",
// required true, primary false).

using System.Text.Json;
using System.Text.Json.Serialization;
using TruePath;
using TruePath.SystemIo;

namespace FlintLauncher.Core.ModPlatform.Flame;

/// <summary>The manifest.json wire shape (the load-side probe; optional members carry the
/// QJsonValue defaults, required ones are validated by the loader).</summary>
internal sealed record FlameManifestJson(
    string? ManifestType,
    int? ManifestVersion,
    FlameMinecraftJson? Minecraft,
    string? Name,
    string? Version,
    string? Author,
    List<FlameFileJson>? Files,
    string? Overrides);

internal sealed record FlameMinecraftJson(
    string? Version,
    string? Libraries,
    List<FlameModloaderJson>? ModLoaders,
    int? RecommendedRam);

internal sealed record FlameModloaderJson(
    string? Id,
    bool? Primary);

internal sealed record FlameFileJson(
    [property: JsonPropertyName("projectID")]
    long? ProjectId,
    [property: JsonPropertyName("fileID")] long? FileId,
    bool? Required);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FlameManifestJson))]
// a JsonSerializerContext of its own: the STJ source generator emits per-primitive helper
// files named "<Context>.<Type>.g.cs", so one context class cannot be declared across two
// files (the second partial would collide on the hint names and abort the whole generator)
internal sealed partial class FlameManifestJsonContext : JsonSerializerContext;

/// <summary>Port of Flame::File.</summary>
public sealed class FlameFile
{
    public long ProjectId { get; init; }
    public long FileId { get; init; }

    // NOTE: the opposite to 'optional'
    public bool Required { get; init; } = true;

    public IndexedPack Pack { get; } = new();
    public IndexedVersion Version { get; set; } = new();

    // our
    public string TargetFolder { get; set; } = "mods";
}

/// <summary>Port of Flame::Modloader.</summary>
public sealed class FlameModloader
{
    public string Id { get; init; } = "";
    public bool Primary { get; init; }
}

/// <summary>Port of Flame::Minecraft (the never-read extra "libraries" member is not
/// ported).</summary>
public sealed class FlameMinecraft
{
    public string Version { get; set; } = "";
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
    /// Throws <see cref="JsonException"/> for non-modpack manifests and unknown versions.</summary>
    public static void LoadManifest(FlameManifest m, AbsolutePath filepath)
    {
        FlameManifestJson? dto;
        try
        {
            dto = JsonSerializer.Deserialize(filepath.ReadAllText(),
                FlameManifestJsonContext.Default.FlameManifestJson);
        }
        catch (JsonException e)
        {
            throw new JsonException("manifest.json: Error parsing JSON: " + e.Message);
        }

        if (dto is null)
        {
            throw new JsonException("manifest.json is not an object");
        }

        LoadManifestV1(m, dto);
    }

    private static void LoadManifestV1(FlameManifest pack, FlameManifestJson manifest)
    {
        pack.ManifestType = Require(manifest.ManifestType, "manifestType");
        if (pack.ManifestType != "minecraftModpack")
        {
            throw new JsonException("Not a modpack manifest!");
        }

        pack.ManifestVersion = Require(manifest.ManifestVersion, "manifestVersion");
        if (pack.ManifestVersion != 1)
        {
            throw new JsonException($"Unknown manifest version ({pack.ManifestVersion})");
        }

        var mc = manifest.Minecraft ?? throw new JsonException("'minecraft' is not an object");
        pack.Minecraft.Version = Require(mc.Version, "version");
        // (the manifest's extra "libraries" member — a 1.2.5 FTB retro curiosity with no
        // reader anywhere — is ignored on the read)
        if (mc.ModLoaders is { } loaders)
        {
            foreach (var loader in loaders)
            {
                pack.Minecraft.ModLoaders.Add(new FlameModloader
                {
                    Id = Require(loader.Id, "id"),
                    Primary = loader.Primary ?? false,
                });
            }
        }

        pack.Minecraft.RecommendedRam = mc.RecommendedRam ?? 0;

        pack.Name = manifest.Name ?? "Unnamed";
        pack.Version = manifest.Version ?? "";
        pack.Author = manifest.Author ?? "Anonymous";

        if (manifest.Files is { } files)
        {
            foreach (var file in files)
            {
                var flameFile = new FlameFile
                {
                    ProjectId = Require(file.ProjectId, "projectID"),
                    FileId = Require(file.FileId, "fileID"),
                    Required = file.Required ?? true,
                };
                pack.Files[flameFile.FileId] = flameFile;
            }
        }

        pack.Overrides = manifest.Overrides ?? "overrides";

        pack.IsLoaded = true;
    }

    // ------------------------------------------------- required-member checks

    private static string Require(string? value, string key) =>
        value ?? throw new JsonException("'" + key + "' is not a string");

    private static T Require<T>(T? value, string key) where T : struct =>
        value ?? throw new JsonException(
            "'" + key + "' is not a " + (typeof(T) == typeof(int) ? "integer" : typeof(T).Name.ToLowerInvariant()));
}
