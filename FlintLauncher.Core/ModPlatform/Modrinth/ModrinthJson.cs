// SPDX-License-Identifier: GPL-3.0-only
// Strongly-typed wire models for the Modrinth v2 API and the .mrpack index format, with the
// source-generated serializer context that owns them (GLOBAL §1: reflection-based
// serialization never enters the picture). The models live beside their only consumers
// (ModrinthApi / ModrinthPackIndex / the pack tasks) instead of a shared JSON namespace.
//
// Serialization notes:
//  - request bodies serialize with WhenWritingNull: a null member means "omit the key",
//    matching the Json::writeString/writeStringList semantics it replaces;
//  - `hashes` maps of versions/projects are keyed by arbitrary algorithm names and stay
//    Dictionary<string, string>;
//  - the POST /version_files (update) responses are hash-keyed maps of versions, so their
//    root type is Dictionary<string, ModrinthVersionJson>.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using FlintLauncher.Core.Json;

namespace FlintLauncher.Core.ModPlatform.Modrinth;

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
/// <summary>The modrinth-download-meta request header payload (net/ApiHeaderProxy).</summary>
public sealed record ModrinthDownloadMetaJson(
    string? Reason,
    string? GameVersion,    string? Loader,
    string? DependentOn);

// ------------------------------------------------------------------------ response models

/// <summary>Modrinth project/search-hit shape (https://docs.modrinth.com GET /project).</summary>
public sealed record ModrinthProjectJson
{
    public string? ProjectId { get; init; }
    public string? Id { get; init; }
    public string? Title { get; init; }
    public string? ProjectType { get; init; }
    public List<string>? Loaders { get; init; }
    public List<string>? AllProjectTypes { get; init; }
    public string? Slug { get; init; }
    public string? Description { get; init; }
    public string? IconUrl { get; init; }
    public string? Author { get; init; }
    public string? ClientSide { get; init; }
    public string? ServerSide { get; init; }
    /// <summary>Search hits only: the raw download count member. Left as JsonElement so the
    /// "only real JSON number tokens count" sniff survives (a quoted "123" stays 0).</summary>
    public JsonElement? Downloads { get; init; }
    public string? IssuesUrl { get; init; }
    public string? SourceUrl { get; init; }
    public string? WikiUrl { get; init; }
    public string? DiscordUrl { get; init; }
    public List<ModrinthDonationJson>? DonationUrls { get; init; }
    public string? Status { get; init; }
    public string? Body { get; init; }}

public sealed record ModrinthDonationJson(
    string? Id,
    string? Platform,
    string? Url);
/// <summary>GET /search body ({hits: [...]}).</summary>
public sealed record ModrinthSearchResponseJson(
    List<ModrinthProjectJson>? Hits);

/// <summary>GET /tag/category entries (the category id is its name).</summary>
public sealed record ModrinthCategoryJson(
    string? Name,
    string? ProjectType,
    string? Icon);

/// <summary>Modrinth version shape (project version lists, hash lookups, update responses).
/// The changelog and dependencies ride on the version object.</summary>
public sealed record ModrinthVersionJson
{
    public string? ProjectId { get; init; }
    public string? Id { get; init; }
    public string? DatePublished { get; init; }
    public List<string>? GameVersions { get; init; }
    public List<string>? Loaders { get; init; }
    public string? Name { get; init; }
    public string? VersionNumber { get; init; }
    public string? VersionType { get; init; }
    public string? Changelog { get; init; }
    public List<ModrinthDependencyJson>? Dependencies { get; init; }

    /// <summary>JSON null elements drop at binding (ModrinthVersionFileListConverter), so
    /// the non-nullable element label holds — docs/JSON-MIGRATION.md §2.4.</summary>
    [property: JsonConverter(typeof(ModrinthVersionFileListConverter))]
    public List<ModrinthVersionFileJson>? Files { get; init; }}

public sealed record ModrinthDependencyJson(
    string? ProjectId,
    string? VersionId,
    string? DependencyType);

/// <summary>One downloadable file of a version; hashes is keyed by algorithm name. A JSON
/// null hash VALUE counts as absent (ModrinthHashMapConverter drops the entry, so the
/// fallback chain keeps walking) — docs/JSON-MIGRATION.md §2.4.</summary>
public sealed record ModrinthVersionFileJson(
    string? Filename,
    bool? Primary,
    string? Url,    long? Size,
    [property: JsonConverter(typeof(ModrinthHashMapConverter))]
    Dictionary<string, string>? Hashes);

// ------------------------------------------------------------------------- .mrpack format

/// <summary>modrinth.index.json (https://docs.modrinth.com/docs/modpacks/format_definition).
/// Shared by the import (parse) and export (write) paths; `dependencies` is a free string map
/// validated against the known-loader whitelist by the import mapping.</summary>
public sealed record MrPackIndexJson
{
    public int? FormatVersion { get; init; }
    public string? Game { get; init; }
    public string? Name { get; init; }
    public string? VersionId { get; init; }
    public string? Summary { get; init; }
    public Dictionary<string, string?>? Dependencies { get; init; }
    public List<MrPackFileJson>? Files { get; init; }}

public sealed record MrPackFileJson(
    MrPackEnvJson? Env,    string? Path,    List<string>? Downloads,
    Dictionary<string, string>? Hashes,
    long? FileSize);
public sealed record MrPackEnvJson(
    string? Client,    string? Server);
// ------------------------------------------------------------------------------- context

// The Modrinth v2 API is snake_case end to end (project_id, date_published, game_versions,
// ...) — the context-wide SnakeCaseLower policy carries the naming; the .mrpack index is
// camelCase (formatVersion / versionId / fileSize) and gets its own camel context.
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ModrinthHashLookupRequest))]
[JsonSerializable(typeof(ModrinthVersionUpdateRequest))]
[JsonSerializable(typeof(ModrinthDownloadMetaJson))]
[JsonSerializable(typeof(ModrinthProjectJson))]
[JsonSerializable(typeof(List<ModrinthProjectJson>))]
[JsonSerializable(typeof(ModrinthSearchResponseJson))]
[JsonSerializable(typeof(ModrinthCategoryJson))]
[JsonSerializable(typeof(List<ModrinthCategoryJson>))]
[JsonSerializable(typeof(ModrinthVersionJson))]
[JsonSerializable(typeof(List<ModrinthVersionJson>))]
[JsonSerializable(typeof(Dictionary<string, ModrinthVersionJson>))]
public sealed partial class ModrinthJsonContext : JsonSerializerContext;

/// <summary>The version files array: JSON null elements are dropped at binding (the
/// non-nullable element label is enforced HERE, in the wire layer — between "non-null +
/// ignore" and "nullable + keep" there is no third option). A wrong-kind member reads
/// null so ModrinthRequire.Array surfaces "'files' is not an array"; junk element kinds
/// still fail the document like a binding error.</summary>
public sealed class ModrinthVersionFileListConverter : JsonConverter<List<ModrinthVersionFileJson>?>
{
    public override List<ModrinthVersionFileJson>? Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType is not JsonTokenType.StartArray)
        {
            return null;
        }

        var files = new List<ModrinthVersionFileJson>();
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

            files.Add(JsonSerializer.Deserialize(ref reader, ModrinthJsonContext.Default.ModrinthVersionFileJson)
                      ?? throw new JsonException("'files' entry is null"));
        }

        throw new JsonException("unterminated array");
    }

    public override void Write(Utf8JsonWriter writer, List<ModrinthVersionFileJson>? value,
        JsonSerializerOptions options) =>
        throw new NotSupportedException("the Modrinth wire records are read-only");
}

/// <summary>The hash map: entries whose JSON value is null drop at binding (treated as
/// absent, so the hash-fallback chain keeps walking — docs/JSON-MIGRATION.md §2.4). A
/// non-string value still fails the document like a binding error.</summary>
public sealed class ModrinthHashMapConverter : JsonConverter<Dictionary<string, string>?>
{
    public override Dictionary<string, string>? Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType is not JsonTokenType.StartObject)
        {
            return null;
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

    public override void Write(Utf8JsonWriter writer, Dictionary<string, string>? value,
        JsonSerializerOptions options) =>
        throw new NotSupportedException("the Modrinth wire records are read-only");
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MrPackIndexJson))]
public sealed partial class MrPackJsonContext : JsonSerializerContext;

/// <summary>Parse helpers over the raw response bodies (the seam where JsonException becomes
/// a null/empty result, preserving the tolerant node-parse outcomes of the former readers).</summary>
public static class ModrinthJson
{
    /// <summary>The {hash: version} lookup responses of POST /version_files(/update). Null
    /// when the body is not a JSON object — every lookup then just misses, like indexing a
    /// non-object node did.</summary>
    public static Dictionary<string, ModrinthVersionJson>? ParseVersionLookup(string json)
    {
        // per-entry tolerance like the former node readers: one malformed version entry
        // costs that entry, not the whole lookup
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (root is not JsonObject obj)
        {
            return null;
        }

        var result = new Dictionary<string, ModrinthVersionJson>(StringComparer.Ordinal);
        foreach (var (hash, node) in obj)
        {
            var version = VersionFromNode(node);
            if (version is not null)
            {
                result[hash] = version;
            }
        }

        return result;
    }

    /// <summary>A JSON array of versions (project version lists), with the former per-entry
    /// tolerance: a non-array body or a malformed entry parses to an empty list / is skipped.
    /// </summary>
    public static List<ModrinthVersionJson> ParseVersionListOrEmpty(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return [];
        }

        if (root is not JsonArray array)
        {
            return [];
        }

        var result = new List<ModrinthVersionJson>();
        foreach (var entry in array)
        {
            var version = VersionFromNode(entry);
            if (version is not null)
            {
                result.Add(version);
            }
        }

        return result;
    }

    public static ModrinthProjectJson? ParseProject(string json) =>
        Parse(json, ModrinthJsonContext.Default.ModrinthProjectJson);

    /// <summary>A JSON array of projects (GET /projects?ids=...). Non-array bodies parse to an
    /// empty list.</summary>
    public static List<ModrinthProjectJson> ParseProjectListOrEmpty(string json) =>
        Parse(json, ModrinthJsonContext.Default.ListModrinthProjectJson) ?? [];

    public static ModrinthSearchResponseJson? ParseSearchResponse(string json) =>
        Parse(json, ModrinthJsonContext.Default.ModrinthSearchResponseJson);

    public static List<ModrinthCategoryJson>? ParseCategories(string json) =>
        Parse(json, ModrinthJsonContext.Default.ListModrinthCategoryJson);

    public static MrPackIndexJson? ParseMrPackIndex(string json) =>
        Parse(json, MrPackJsonContext.Default.MrPackIndexJson);

    /// <summary>Deserialize a version DTO from an already-parsed node (the provider-dispatch
    /// seams that sniff the response shape before knowing the provider). Null when the node
    /// is not a version object.</summary>
    public static ModrinthVersionJson? VersionFromNode(JsonNode? node) =>
        FromNode(node, ModrinthJsonContext.Default.ModrinthVersionJson);

    /// <summary>Deserialize a project DTO from an already-parsed node (same dispatch seams).</summary>
    public static ModrinthProjectJson? ProjectFromNode(JsonNode? node) =>
        FromNode(node, ModrinthJsonContext.Default.ModrinthProjectJson);

    /// <summary>Deserialize a version array from an already-parsed node; null when the node
    /// is not an array (the array sniff of the former readers).</summary>
    public static List<ModrinthVersionJson>? VersionListFromNode(JsonNode? node) =>
        FromNode(node, ModrinthJsonContext.Default.ListModrinthVersionJson);

    /// <summary>Deserialize a project array from an already-parsed node; null when the node
    /// is not an array.</summary>
    public static List<ModrinthProjectJson>? ProjectListFromNode(JsonNode? node) =>
        FromNode(node, ModrinthJsonContext.Default.ListModrinthProjectJson);

    private static T? FromNode<T>(JsonNode? node, JsonTypeInfo<T> typeInfo) where T : class
    {
        if (node is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(node, typeInfo);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static T? Parse<T>(string json, JsonTypeInfo<T> typeInfo) where T : class
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

/// <summary>Required-member checks over the parsed DTOs, mirroring the former requireIsType*
/// messages the former node readers produced (the "key is not a …" half; the DTO cannot tell
/// a missing key from a wrong-kind one, so both collapse onto the kind message).</summary>
internal static class ModrinthRequire
{
    public static string String(string? value, string key) =>
        value ?? throw new JsonParseException("'" + key + "' is not a string");

    public static bool Boolean(bool? value, string key) =>
        value ?? throw new JsonParseException("'" + key + "' is not a bool");

    public static List<T> Array<T>(List<T>? value, string key) =>
        value ?? throw new JsonParseException("'" + key + "' is not an array");

    public static Dictionary<string, string> Object(Dictionary<string, string>? value, string key) =>
        value ?? throw new JsonParseException("'" + key + "' is not an object");
}
