// SPDX-License-Identifier: GPL-3.0-only
// Strongly-typed wire models for the Modrinth v2 API and the .mrpack index format, with the
// source-generated serializer context that owns them (GLOBAL §1: reflection-based
// serialization never enters the picture). The models live beside their only consumers
// (ModrinthApi / ModrinthPackIndex / the pack tasks) instead of a shared JSON namespace.
//
// Serialization notes:
//  - request bodies serialize with WhenWritingNull: a null member means "omit the key",
//    matching the JsonUtils.WriteString/WriteStringList semantics it replaces;
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
    [property: JsonPropertyName("hashes")] List<string>? Hashes,
    [property: JsonPropertyName("algorithm")] string? Algorithm);

/// <summary>POST /version_file/{hash}/update and POST /version_files/update bodies; the
/// hashes/algorithm members are only set by the batch form.</summary>
public sealed record ModrinthVersionUpdateRequest(
    [property: JsonPropertyName("hashes")] List<string>? Hashes,
    [property: JsonPropertyName("algorithm")] string? Algorithm,
    [property: JsonPropertyName("loaders")] List<string>? Loaders,
    [property: JsonPropertyName("game_versions")] List<string>? GameVersions,
    [property: JsonPropertyName("version_types")] List<string>? VersionTypes);

/// <summary>The modrinth-download-meta request header payload (net/ApiHeaderProxy).</summary>
public sealed record ModrinthDownloadMetaJson(
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("game_version")] string? GameVersion,
    [property: JsonPropertyName("loader")] string? Loader,
    [property: JsonPropertyName("dependent_on")] string? DependentOn);

// ------------------------------------------------------------------------ response models

/// <summary>Modrinth project/search-hit shape (https://docs.modrinth.com GET /project).</summary>
public sealed record ModrinthProjectJson
{
    [JsonPropertyName("project_id")] public string? ProjectId { get; init; }

    [JsonPropertyName("id")] public string? Id { get; init; }

    [JsonPropertyName("title")] public string? Title { get; init; }

    [JsonPropertyName("project_type")] public string? ProjectType { get; init; }

    [JsonPropertyName("loaders")] public List<string>? Loaders { get; init; }

    [JsonPropertyName("all_project_types")] public List<string>? AllProjectTypes { get; init; }

    [JsonPropertyName("slug")] public string? Slug { get; init; }

    [JsonPropertyName("description")] public string? Description { get; init; }

    [JsonPropertyName("icon_url")] public string? IconUrl { get; init; }

    [JsonPropertyName("author")] public string? Author { get; init; }

    [JsonPropertyName("client_side")] public string? ClientSide { get; init; }

    [JsonPropertyName("server_side")] public string? ServerSide { get; init; }

    /// <summary>Search hits only: the raw download count member. Left as JsonElement so the
    /// "only real JSON number tokens count" sniff survives (a quoted "123" stays 0).</summary>
    [JsonPropertyName("downloads")] public JsonElement? Downloads { get; init; }

    [JsonPropertyName("issues_url")] public string? IssuesUrl { get; init; }

    [JsonPropertyName("source_url")] public string? SourceUrl { get; init; }

    [JsonPropertyName("wiki_url")] public string? WikiUrl { get; init; }

    [JsonPropertyName("discord_url")] public string? DiscordUrl { get; init; }

    [JsonPropertyName("donation_urls")] public List<ModrinthDonationJson>? DonationUrls { get; init; }

    [JsonPropertyName("status")] public string? Status { get; init; }

    [JsonPropertyName("body")] public string? Body { get; init; }
}

public sealed record ModrinthDonationJson(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("platform")] string? Platform,
    [property: JsonPropertyName("url")] string? Url);

/// <summary>GET /search body ({hits: [...]}).</summary>
public sealed record ModrinthSearchResponseJson(
    [property: JsonPropertyName("hits")] List<ModrinthProjectJson>? Hits);

/// <summary>GET /tag/category entries (the category id is its name).</summary>
public sealed record ModrinthCategoryJson(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("project_type")] string? ProjectType,
    [property: JsonPropertyName("icon")] string? Icon);

/// <summary>Modrinth version shape (project version lists, hash lookups, update responses).
/// The changelog and dependencies ride on the version object.</summary>
public sealed record ModrinthVersionJson
{
    [JsonPropertyName("project_id")] public string? ProjectId { get; init; }

    [JsonPropertyName("id")] public string? Id { get; init; }

    [JsonPropertyName("date_published")] public string? DatePublished { get; init; }

    [JsonPropertyName("game_versions")] public List<string>? GameVersions { get; init; }

    [JsonPropertyName("loaders")] public List<string>? Loaders { get; init; }

    [JsonPropertyName("name")] public string? Name { get; init; }

    [JsonPropertyName("version_number")] public string? VersionNumber { get; init; }

    [JsonPropertyName("version_type")] public string? VersionType { get; init; }

    [JsonPropertyName("changelog")] public string? Changelog { get; init; }

    [JsonPropertyName("dependencies")] public List<ModrinthDependencyJson>? Dependencies { get; init; }

    [JsonPropertyName("files")] public List<ModrinthVersionFileJson>? Files { get; init; }
}

public sealed record ModrinthDependencyJson(
    [property: JsonPropertyName("project_id")] string? ProjectId,
    [property: JsonPropertyName("version_id")] string? VersionId,
    [property: JsonPropertyName("dependency_type")] string? DependencyType);

/// <summary>One downloadable file of a version; hashes is keyed by algorithm name.</summary>
public sealed record ModrinthVersionFileJson(
    [property: JsonPropertyName("filename")] string? Filename,
    [property: JsonPropertyName("primary")] bool? Primary,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("size")] long? Size,
    [property: JsonPropertyName("hashes")] Dictionary<string, string>? Hashes);

// ------------------------------------------------------------------------- .mrpack format

/// <summary>modrinth.index.json (https://docs.modrinth.com/docs/modpacks/format_definition).
/// Shared by the import (parse) and export (write) paths; `dependencies` is a free string map
/// validated against the known-loader whitelist by the import mapping.</summary>
public sealed record MrPackIndexJson
{
    [JsonPropertyName("formatVersion")] public int? FormatVersion { get; init; }

    [JsonPropertyName("game")] public string? Game { get; init; }

    [JsonPropertyName("name")] public string? Name { get; init; }

    [JsonPropertyName("versionId")] public string? VersionId { get; init; }

    [JsonPropertyName("summary")] public string? Summary { get; init; }

    [JsonPropertyName("dependencies")] public Dictionary<string, string?>? Dependencies { get; init; }

    [JsonPropertyName("files")] public List<MrPackFileJson>? Files { get; init; }
}

public sealed record MrPackFileJson(
    [property: JsonPropertyName("env")] MrPackEnvJson? Env,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("downloads")] List<string>? Downloads,
    [property: JsonPropertyName("hashes")] Dictionary<string, string>? Hashes,
    [property: JsonPropertyName("fileSize")] long? FileSize);

public sealed record MrPackEnvJson(
    [property: JsonPropertyName("client")] string? Client,
    [property: JsonPropertyName("server")] string? Server);

// ------------------------------------------------------------------------------- context

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
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
[JsonSerializable(typeof(MrPackIndexJson))]
public sealed partial class ModrinthJsonContext : JsonSerializerContext;

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
        Parse(json, ModrinthJsonContext.Default.MrPackIndexJson);

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

/// <summary>Required-member checks over the parsed DTOs, mirroring the JsonUtils.Require*
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
