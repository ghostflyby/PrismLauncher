// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2021 Jamie Mansfield <jmansfield@cadixdev.org>
// SPDX-FileCopyrightText: 2021-2022 Jamie Mansfield <jmansfield@cadixdev.org>
//
// This file incorporates work covered by the following copyright and
// permission notice:
//
//     Copyright 2020-2021 MultiMC Contributors
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
// The Technic Platform and Solder API wire models with their decode — the response half of
// the Technic page/model (ui/pages/modplatform/technic/TechnicModel.cpp + TechnicPage.cpp)
// and of modplatform/technic/SolderPackManifest. The platform documents decode tolerantly
// like the C++ readers (a malformed document reads as nothing, never a failure); a Solder
// build is strict, its failures surface with the "JSON response from Solder" wrapper.

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace FlintLauncher.ExternalApi.Technic;

/// <summary>Port of the Technic::ListModel search modes: the list documents (trending and
/// search) or the single-pack document.</summary>
public enum TechnicSearchMode : byte
{
    List,
    Single,
}

/// <summary>The trending/search list root.</summary>
public sealed record TechnicSearchListJson(
    List<TechnicListEntryJson>? Modpacks);

/// <summary>One trending/search entry; a null iconUrl carries the literal "null" sentinel
/// (the caller maps it to the null-logo placeholder).</summary>
public sealed record TechnicListEntryJson(
    string? Name,
    string? Slug,
    string? IconUrl);

/// <summary>The single-pack response; an "error" member marks the invalid-API-url
/// answer.</summary>
public sealed record TechnicSinglePackJson(
    string? DisplayName = null,
    string? Name = null,
    TechnicIconJson? Icon = null,
    string? Error = null);

public sealed record TechnicIconJson(
    string? Url);

/// <summary>The pack metadata response: a string "url" (direct zip) wins over a string
/// "solder" (Solder API root); neither fails the load.</summary>
public sealed record TechnicMetadataJson(
    string? Url,
    string? Solder,
    string? Minecraft,
    string? PlatformUrl,
    string? User,
    string? Description,
    string? Version,
    string? DisplayName = null,
    string? Name = null);

/// <summary>A search answer in the shape its mode selected: <see cref="List"/> for the list
/// documents, <see cref="Single"/> for the single-pack document; the member is null when
/// the document was malformed.</summary>
public sealed record TechnicSearchResponse(
    TechnicSearchMode Mode,
    TechnicSearchListJson? List,
    TechnicSinglePackJson? Single);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TechnicSearchListJson))]
[JsonSerializable(typeof(TechnicSinglePackJson))]
[JsonSerializable(typeof(TechnicMetadataJson))]
[JsonSerializable(typeof(SolderPack))]
[JsonSerializable(typeof(SolderPackBuild))]
public sealed partial class TechnicJsonContext : JsonSerializerContext;

/// <summary>The document decode of the Technic and Solder payloads — the entry points the
/// operations read responses through and callers reuse for out-of-band payloads.</summary>
public static class TechnicJson
{
    /// <summary>Decodes a search answer in the shape of <paramref name="mode"/>; a malformed
    /// document leaves the member null (ListModel::searchRequestFinished appends nothing).</summary>
    public static TechnicSearchResponse LoadSearch(string response, TechnicSearchMode mode) => mode switch
    {
        TechnicSearchMode.Single => new TechnicSearchResponse(mode, null,
            Tolerant(response, TechnicJsonContext.Default.TechnicSinglePackJson)),
        _ => new TechnicSearchResponse(mode,
            Tolerant(response, TechnicJsonContext.Default.TechnicSearchListJson), null),
    };

    /// <summary>Decodes the pack metadata (TechnicPage::metadataLoaded); a malformed document
    /// reads as null.</summary>
    public static TechnicMetadataJson? LoadMetadata(string response) =>
        Tolerant(response, TechnicJsonContext.Default.TechnicMetadataJson);

    /// <summary>Decodes a Solder pack listing (TechnicSolder::loadPack); a malformed document
    /// reads as null (the C++ falls back to the current version).</summary>
    public static SolderPack? LoadSolderPack(string response) =>
        Tolerant(response, TechnicJsonContext.Default.SolderPack);

    /// <summary>Decodes a Solder build (TechnicSolder::loadPackBuild, the
    /// SolderPackInstallTask entry); any failure throws <see cref="JsonException"/> with the
    /// Solder wrapper.</summary>
    public static SolderPackBuild LoadSolderPackBuild(string response)
    {
        SolderPackBuild? build;
        try
        {
            build = JsonSerializer.Deserialize(response, TechnicJsonContext.Default.SolderPackBuild);
        }
        catch (JsonException e)
        {
            throw new JsonException("JSON response from Solder: Error parsing JSON: " + e.Message);
        }

        if (build is null)
        {
            throw new JsonException("JSON response from Solder: Error parsing JSON: document is null");
        }

        // JSON null members/elements are not enforced by the binding — surface them like the
        // former Require reads instead of letting them NRE the install task
        if (build.Minecraft is null)
        {
            throw new JsonException("'minecraft' is not a string");
        }

        foreach (var mod in build.Mods)
        {
            // a null ELEMENT or a null url both surface as the former RequireObject check
            if (mod?.Url is null)
            {
                throw new JsonException("Value is not an object");
            }

            if (mod.Md5 is null)
            {
                throw new JsonException("'md5' is not a string");
            }
        }

        return build;
    }

    /// <summary>The tolerant document read: a malformed document reads as null.</summary>
    private static T? Tolerant<T>(string json, JsonTypeInfo<T> typeInfo) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(json, typeInfo);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
