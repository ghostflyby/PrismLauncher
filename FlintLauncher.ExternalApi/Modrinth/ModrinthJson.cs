// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Strongly-typed wire models for the Modrinth v2 API and the .mrpack index format, with the
// source-generated serializer context that owns them (reflection-based serialization never
// enters the picture). Shapes and nullability follow the labrinth OpenAPI document: the
// spec's required members are non-nullable C# `required` properties (a missing key fails
// the document), genuinely optional members stay nullable, and search hits are their own
// type (ProjectResult) separate from the full Project — the spec has no author on Project
// and no all_project_types anywhere. Only the members this codebase consumes are modeled;
// the serializer ignores the rest.
//
// Serialization notes:
//  - request bodies serialize with WhenWritingNull: a null member means "omit the key";
//  - `hashes` maps are keyed by arbitrary algorithm names and stay Dictionary<string, string>;
//  - the POST /version_files (update) responses are hash-keyed maps of versions.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlintLauncher.ExternalApi;

// ------------------------------------------------------------------------- request bodies

/// <summary>POST /version_files body (hash list lookup).</summary>
public sealed record ModrinthHashLookupRequest(
    List<string>? Hashes,
    string? Algorithm);

/// <summary>POST /version_file/{hash}/update and POST /version_files/update bodies; the
/// hashes/algorithm members are only set by the batch form.</summary>
public sealed record ModrinthVersionUpdateRequest(
    List<string>? Hashes,
    string? Algorithm,
    List<string>? Loaders,
    List<string>? GameVersions,
    List<string>? VersionTypes);

/// <summary>The modrinth-download-meta request header payload. The header is only sent
/// when at least the reason carries content.</summary>
public sealed record ModrinthDownloadMeta(
    string? Reason,
    string? GameVersion,
    string? Loader,
    string? DependentOn)
{
    // default(DownloadMeta) produces null fields; treat them like absent strings
    public bool IsEmpty => string.IsNullOrEmpty(Reason);
}

// ------------------------------------------------------------------------ response models

/// <summary>The full project shape (GET /project/{id}, POST /projects?ids=[]) — the spec's
/// Project, minus the members nothing here consumes. `icon_url` is explicitly nullable;
/// the URLs below are optional links.</summary>
public sealed record ModrinthProject
{
    public required string Id { get; init; }
    public required string Slug { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required string Body { get; init; }
    public required string Status { get; init; }
    public required string ProjectType { get; init; }
    public required string ClientSide { get; init; }
    public required string ServerSide { get; init; }

    public string? IconUrl { get; init; }

    /// <summary>Not in the Project schema's required list.</summary>
    public List<string>? Loaders { get; init; }

    public string? IssuesUrl { get; init; }
    public string? SourceUrl { get; init; }
    public string? WikiUrl { get; init; }
}

/// <summary>One search hit (the spec's ProjectResult) — a distinct type from the full
/// project: `author` exists on search results only. Only the consumed members are modeled;
/// the spec types `downloads` as a plain integer.</summary>
public sealed record ModrinthSearchResult
{
    public required string ProjectId { get; init; }
    public required string Slug { get; init; }
    public required string Title { get; init; }
    public required string Author { get; init; }
    public required long Downloads { get; init; }

    public string? IconUrl { get; init; }
}

/// <summary>GET /search envelope (the spec's SearchResults); the pagination members are not
/// modeled.</summary>
public sealed record ModrinthSearchResults
{
    public required List<ModrinthSearchResult> Hits { get; init; }
}

/// <summary>GET /tag/category entries (the spec's CategoryTag; `header` is required).</summary>
public sealed record ModrinthCategory
{
    public required string Name { get; init; }
    public required string ProjectType { get; init; }
    public required string Icon { get; init; }
    public required string Header { get; init; }
}

/// <summary>The version shape (project version lists, hash lookups, update responses).
/// Required members follow the spec's Version required list; `changelog` and
/// `dependencies` stay optional.</summary>
public sealed record ModrinthVersion
{
    public required string ProjectId { get; init; }
    public required string Id { get; init; }
    public required string DatePublished { get; init; }
    public required List<string> GameVersions { get; init; }
    public required List<string> Loaders { get; init; }
    public required string Name { get; init; }
    public required string VersionNumber { get; init; }
    public required string VersionType { get; init; }

    public string? Changelog { get; init; }
    public List<ModrinthDependency>? Dependencies { get; init; }

    /// <summary>JSON null elements drop at binding (ModrinthVersionFileListConverter), so
    /// the non-nullable element label holds.</summary>
    [property: JsonConverter(typeof(ModrinthVersionFileListConverter))]
    public required List<ModrinthVersionFile> Files { get; init; }
}

/// <summary>The spec's VersionDependency: at most one of version_id/project_id is set;
/// `dependency_type` is the only required member.</summary>
public sealed record ModrinthDependency
{
    public string? ProjectId { get; init; }
    public string? VersionId { get; init; }
    public required string DependencyType { get; init; }
}

/// <summary>One downloadable file of a version (the spec's VersionFile: hashes, url,
/// filename, primary and size are all required). A JSON null hash VALUE counts as absent
/// (ModrinthHashMapConverter drops the entry, so the fallback chain keeps walking).</summary>
public sealed record ModrinthVersionFile
{
    public required string Filename { get; init; }
    public required bool Primary { get; init; }
    public required string Url { get; init; }
    public required long Size { get; init; }

    [property: JsonConverter(typeof(ModrinthHashMapConverter))]
    public required Dictionary<string, string> Hashes { get; init; }
}

// ------------------------------------------------------------------------- .mrpack format

/// <summary>modrinth.index.json (https://docs.modrinth.com/docs/modpacks/format_definition).
/// Shared by the import (parse) and export (write) paths; `dependencies` is a free string map
/// validated against the known-loader whitelist by the import mapping.</summary>
public sealed record MrPackIndex
{
    public int? FormatVersion { get; init; }
    public string? Game { get; init; }
    public string? Name { get; init; }
    public string? VersionId { get; init; }
    public string? Summary { get; init; }
    public Dictionary<string, string?>? Dependencies { get; init; }
    public List<MrPackFile>? Files { get; init; }
}

public sealed record MrPackFile(
    MrPackEnv? Env,
    string? Path,
    List<string>? Downloads,
    Dictionary<string, string>? Hashes,
    long? FileSize);

public sealed record MrPackEnv(
    string? Client,
    string? Server);
// ------------------------------------------------------------------------------- context

// The Modrinth v2 API is snake_case end to end (project_id, date_published, game_versions,
// ...) — the context-wide SnakeCaseLower policy carries the naming; the .mrpack index is
// camelCase (formatVersion / versionId / fileSize) and gets its own camel context.
// RespectNullableAnnotations closes the presence-only gap of `required`: a present-but-null
// value for a non-nullable member fails the decode with a named JsonException instead of
// landing as a NullReferenceException downstream. (Property-level only — the source
// generator does not emit element-level checks for collection members.)
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    RespectNullableAnnotations = true)]
[JsonSerializable(typeof(ModrinthHashLookupRequest))]
[JsonSerializable(typeof(ModrinthVersionUpdateRequest))]
[JsonSerializable(typeof(ModrinthDownloadMeta))]
[JsonSerializable(typeof(ModrinthProject))]
[JsonSerializable(typeof(List<ModrinthProject>))]
[JsonSerializable(typeof(ModrinthSearchResults))]
[JsonSerializable(typeof(ModrinthCategory))]
[JsonSerializable(typeof(List<ModrinthCategory>))]
[JsonSerializable(typeof(ModrinthVersion))]
[JsonSerializable(typeof(List<ModrinthVersion>))]
[JsonSerializable(typeof(Dictionary<string, ModrinthVersion>))]
[JsonSerializable(typeof(List<List<string>>))]
public sealed partial class ModrinthJsonContext : JsonSerializerContext;

/// <summary>The version files array: JSON null elements are dropped at binding (the
/// non-nullable element label is enforced HERE, in the wire layer — between "non-null +
/// ignore" and "nullable + keep" there is no third option). A non-array or null token fails
/// the document like a binding error.</summary>
public sealed class ModrinthVersionFileListConverter : JsonConverter<List<ModrinthVersionFile>>
{
    public override List<ModrinthVersionFile> Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType is not JsonTokenType.StartArray)
        {
            throw new JsonException("'files' is not an array");
        }

        var files = new List<ModrinthVersionFile>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return files;
            }

            if (reader.TokenType == JsonTokenType.Null)
            {
                continue;
            }

            files.Add(JsonSerializer.Deserialize(ref reader, ModrinthJsonContext.Default.ModrinthVersionFile)
                      ?? throw new JsonException("'files' entry is null"));
        }

        throw new JsonException("unterminated array");
    }

    public override void Write(Utf8JsonWriter writer, List<ModrinthVersionFile> value,
        JsonSerializerOptions options) =>
        throw new NotSupportedException("the Modrinth wire models are read-only");
}

/// <summary>The hash map: entries whose JSON value is null drop at binding (treated as
/// absent, so the hash-fallback chain keeps walking). A non-object token or a non-string
/// value fails the document.</summary>
public sealed class ModrinthHashMapConverter : JsonConverter<Dictionary<string, string>>
{
    public override Dictionary<string, string> Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType is not JsonTokenType.StartObject)
        {
            throw new JsonException("'hashes' is not an object");
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return map;
            }

            var key = reader.GetString() ?? "";
            reader.Read();
            if (reader.TokenType == JsonTokenType.Null)
            {
                continue;
            }

            map[key] = reader.TokenType == JsonTokenType.String
                ? reader.GetString() ?? ""
                : throw new JsonException($"'{key}' is not a string");
        }

        throw new JsonException("unterminated object");
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, string> value,
        JsonSerializerOptions options) =>
        throw new NotSupportedException("the Modrinth wire models are read-only");
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectNullableAnnotations = true)]
[JsonSerializable(typeof(MrPackIndex))]
public sealed partial class MrPackJsonContext : JsonSerializerContext;
