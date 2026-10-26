// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// The Modrinth v2 endpoints as operation values: each endpoint is a struct bound to the
// client (the navigation root whose Options carry the base URL) plus its arguments, and
// knows how to build its request and read its response. The client exposes them through
// per-area groups (Versions / Projects / Tags / Search) whose methods return the values;
// sending goes through the shared ApiOperation pipeline. The JSON readers decode with the
// strict source-generated context (RespectNullableAnnotations) and share the status gate;
// the description endpoint owns its own rule: any failed status degrades to an empty body.

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace FlintLauncher.ExternalApi;

/// <summary>The shared decode of the JSON operations: the success gate, then strict
/// source-generated binding — a JSON null document fails like any shape mismatch.</summary>
internal static class ModrinthDecoding
{
    internal static T ReadJson<T>(HttpRequestMessage request, HttpResponseMessage response, byte[] body,
        JsonTypeInfo<T> typeInfo) where T : class
    {
        ApiOperation.EnsureSuccess(request, response, body);
        return JsonSerializer.Deserialize(body, typeInfo) ?? throw new JsonException("the response body is null");
    }
}

// ------------------------------------------------------------------------------ versions

/// <summary>GET /version_file/{hash}?algorithm= — the version for one file hash.</summary>
public readonly record struct ModrinthFileVersionQuery(
    ModrinthClient Root, string Hash, string Algorithm) : IApiOperation<ModrinthVersion>
{
    public IApiExecutor Executor => Root;

    public HttpRequestMessage CreateRequest() =>
        new(HttpMethod.Get, $"{Root.Options.BaseUrl}/version_file/{Hash}?algorithm={Algorithm}");

    public ValueTask<ModrinthVersion> ReadResponseAsync(HttpRequestMessage request,
        HttpResponseMessage response, byte[] body, CancellationToken ct) =>
        ValueTask.FromResult(ModrinthDecoding.ReadJson(request, response, body,
            ModrinthJsonContext.Default.ModrinthVersion));
}

/// <summary>POST /version_files — the versions for a batch of file hashes; an empty hash
/// list or algorithm is omitted from the body.</summary>
public readonly record struct ModrinthFileVersionLookupQuery(
    ModrinthClient Root, IReadOnlyList<string> Hashes, string Algorithm)
    : IApiOperation<Dictionary<string, ModrinthVersion>>
{
    public IApiExecutor Executor => Root;

    public HttpRequestMessage CreateRequest()
    {
        var body = new ModrinthHashLookupRequest(
            Hashes: Hashes.Count != 0 ? [.. Hashes] : null,
            Algorithm: Algorithm.Length != 0 ? Algorithm : null);
        return new HttpRequestMessage(HttpMethod.Post, $"{Root.Options.BaseUrl}/version_files")
        {
            Content = new ByteArrayContent(
                JsonSerializer.SerializeToUtf8Bytes(body, ModrinthJsonContext.Default.ModrinthHashLookupRequest)),
        };
    }

    public ValueTask<Dictionary<string, ModrinthVersion>> ReadResponseAsync(HttpRequestMessage request,
        HttpResponseMessage response, byte[] body, CancellationToken ct) =>
        ValueTask.FromResult(ModrinthDecoding.ReadJson(request, response, body,
            ModrinthJsonContext.Default.DictionaryStringModrinthVersion));
}

/// <summary>POST /version_files/update — the latest versions for a batch of file hashes;
/// the optional lists shape the body and are omitted when empty (#3404).</summary>
public readonly record struct ModrinthLatestVersionsQuery(
    ModrinthClient Root, IReadOnlyList<string> Hashes, string Algorithm,
    List<string>? Loaders, List<string>? GameVersions, List<string>? VersionTypes)
    : IApiOperation<Dictionary<string, ModrinthVersion>>
{
    public IApiExecutor Executor => Root;

    public HttpRequestMessage CreateRequest()
    {
        var body = new ModrinthVersionUpdateRequest(
            Hashes: Hashes is { Count: > 0 } ? [.. Hashes] : null,
            Algorithm: Algorithm is { Length: > 0 } ? Algorithm : null,
            Loaders: Loaders,
            GameVersions: GameVersions,
            VersionTypes: VersionTypes);
        return new HttpRequestMessage(HttpMethod.Post, $"{Root.Options.BaseUrl}/version_files/update")
        {
            Content = new ByteArrayContent(
                JsonSerializer.SerializeToUtf8Bytes(body, ModrinthJsonContext.Default.ModrinthVersionUpdateRequest)),
        };
    }

    public ValueTask<Dictionary<string, ModrinthVersion>> ReadResponseAsync(HttpRequestMessage request,
        HttpResponseMessage response, byte[] body, CancellationToken ct) =>
        ValueTask.FromResult(ModrinthDecoding.ReadJson(request, response, body,
            ModrinthJsonContext.Default.DictionaryStringModrinthVersion));
}

/// <summary>GET /version/{id} — one pinned version (the dependency lookup).</summary>
public readonly record struct ModrinthVersionQuery(ModrinthClient Root, string VersionId)
    : IApiOperation<ModrinthVersion>
{
    public IApiExecutor Executor => Root;

    public HttpRequestMessage CreateRequest() =>
        new(HttpMethod.Get, $"{Root.Options.BaseUrl}/version/{VersionId}");

    public ValueTask<ModrinthVersion> ReadResponseAsync(HttpRequestMessage request,
        HttpResponseMessage response, byte[] body, CancellationToken ct) =>
        ValueTask.FromResult(ModrinthDecoding.ReadJson(request, response, body,
            ModrinthJsonContext.Default.ModrinthVersion));
}

/// <summary>GET a version-list URL — the array of versions behind a project's /version
/// endpoint; the query string is built by the caller (the Core adapter owns the
/// game-version/loader filters).</summary>
public readonly record struct ModrinthVersionListQuery(ModrinthClient Root, string VersionsUrl)
    : IApiOperation<List<ModrinthVersion>>
{
    public IApiExecutor Executor => Root;

    public HttpRequestMessage CreateRequest() => new(HttpMethod.Get, VersionsUrl);

    public ValueTask<List<ModrinthVersion>> ReadResponseAsync(HttpRequestMessage request,
        HttpResponseMessage response, byte[] body, CancellationToken ct) =>
        ValueTask.FromResult(ModrinthDecoding.ReadJson(request, response, body,
            ModrinthJsonContext.Default.ListModrinthVersion));
}

// ------------------------------------------------------------------------------ projects

/// <summary>GET /project/{id} — one project.</summary>
public readonly record struct ModrinthProjectQuery(ModrinthClient Root, string Id)
    : IApiOperation<ModrinthProject>
{
    public IApiExecutor Executor => Root;

    public HttpRequestMessage CreateRequest() =>
        new(HttpMethod.Get, $"{Root.Options.BaseUrl}/project/{Id}");

    public ValueTask<ModrinthProject> ReadResponseAsync(HttpRequestMessage request,
        HttpResponseMessage response, byte[] body, CancellationToken ct) =>
        ValueTask.FromResult(ModrinthDecoding.ReadJson(request, response, body,
            ModrinthJsonContext.Default.ModrinthProject));
}

/// <summary>GET /projects?ids=[...] — the projects for a batch of ids.</summary>
public readonly record struct ModrinthProjectsQuery(ModrinthClient Root, IReadOnlyList<string> Ids)
    : IApiOperation<List<ModrinthProject>>
{
    public IApiExecutor Executor => Root;

    public HttpRequestMessage CreateRequest() => new(HttpMethod.Get,
        Root.Options.BaseUrl + $"/projects?ids=[\"{string.Join("\",\"", Ids)}\"]");

    public ValueTask<List<ModrinthProject>> ReadResponseAsync(HttpRequestMessage request,
        HttpResponseMessage response, byte[] body, CancellationToken ct) =>
        ValueTask.FromResult(ModrinthDecoding.ReadJson(request, response, body,
            ModrinthJsonContext.Default.ListModrinthProject));
}

/// <summary>GET /project/{id}/description — the project body as plain text. The endpoint
/// owns its status rule: any failed status degrades to "" (Modrinth has no per-file
/// changelog endpoint — changelogs ride on the version objects).</summary>
public readonly record struct ModrinthDescriptionQuery(ModrinthClient Root, string Id)
    : IApiOperation<string>
{
    public IApiExecutor Executor => Root;

    public HttpRequestMessage CreateRequest() =>
        new(HttpMethod.Get, $"{Root.Options.BaseUrl}/project/{Id}/description");

    public ValueTask<string> ReadResponseAsync(HttpRequestMessage request,
        HttpResponseMessage response, byte[] body, CancellationToken ct) =>
        ValueTask.FromResult(response.IsSuccessStatusCode ? Encoding.UTF8.GetString(body) : "");
}

// ------------------------------------------------------------------------ tags & search

/// <summary>GET /tag/category.</summary>
public readonly record struct ModrinthCategoriesQuery(ModrinthClient Root)
    : IApiOperation<List<ModrinthCategory>>
{
    public IApiExecutor Executor => Root;

    public HttpRequestMessage CreateRequest() =>
        new(HttpMethod.Get, $"{Root.Options.BaseUrl}/tag/category");

    public ValueTask<List<ModrinthCategory>> ReadResponseAsync(HttpRequestMessage request,
        HttpResponseMessage response, byte[] body, CancellationToken ct) =>
        ValueTask.FromResult(ModrinthDecoding.ReadJson(request, response, body,
            ModrinthJsonContext.Default.ListModrinthCategory));
}

/// <summary>GET /search — the search envelope; the query string is built by the caller
/// (the Core adapter owns the facet building).</summary>
public readonly record struct ModrinthSearchQuery(ModrinthClient Root, string SearchUrl)
    : IApiOperation<ModrinthSearchResults>
{
    public IApiExecutor Executor => Root;

    public HttpRequestMessage CreateRequest() => new(HttpMethod.Get, SearchUrl);

    public ValueTask<ModrinthSearchResults> ReadResponseAsync(HttpRequestMessage request,
        HttpResponseMessage response, byte[] body, CancellationToken ct) =>
        ValueTask.FromResult(ModrinthDecoding.ReadJson(request, response, body,
            ModrinthJsonContext.Default.ModrinthSearchResults));
}

// -------------------------------------------------------------------------------- groups

/// <summary>The version endpoints: file-hash lookups, the batch update check and the
/// version-list fetch, each returning an operation value bound to the client.</summary>
public readonly struct ModrinthVersionQueries(ModrinthClient root)
{
    /// <summary>GET /version_file/{hash}?algorithm=.</summary>
    public ModrinthFileVersionQuery ByFileHash(string hash, string algorithm) => new(root, hash, algorithm);

    /// <summary>POST /version_files.</summary>
    public ModrinthFileVersionLookupQuery ByFileHashes(IReadOnlyList<string> hashes, string algorithm) =>
        new(root, hashes, algorithm);

    /// <summary>POST /version_files/update.</summary>
    public ModrinthLatestVersionsQuery Latest(IReadOnlyList<string> hashes, string hashFormat,
        List<string>? loaders, List<string>? gameVersions, List<string>? versionTypes) =>
        new(root, hashes, hashFormat, loaders, gameVersions, versionTypes);

    /// <summary>GET /version/{id}.</summary>
    public ModrinthVersionQuery ById(string versionId) => new(root, versionId);

    /// <summary>GET a version-list URL (the Core adapter builds the query string).</summary>
    public ModrinthVersionListQuery At(string versionsUrl) => new(root, versionsUrl);
}

/// <summary>The project endpoints: single and batch project fetches plus the description
/// body, each returning an operation value bound to the client.</summary>
public readonly struct ModrinthProjectQueries(ModrinthClient root)
{
    /// <summary>GET /project/{id}.</summary>
    public ModrinthProjectQuery ById(string id) => new(root, id);

    /// <summary>GET /projects?ids=[...].</summary>
    public ModrinthProjectsQuery ByIds(IReadOnlyList<string> ids) => new(root, ids);

    /// <summary>GET /project/{id}/description (a failed status degrades to "").</summary>
    public ModrinthDescriptionQuery Description(string id) => new(root, id);
}

/// <summary>The tag endpoints.</summary>
public readonly struct ModrinthTagQueries(ModrinthClient root)
{
    /// <summary>GET /tag/category.</summary>
    public ModrinthCategoriesQuery Categories() => new(root);
}

/// <summary>The search endpoint.</summary>
public readonly struct ModrinthSearchQueries(ModrinthClient root)
{
    /// <summary>GET a search URL (the Core adapter builds the query string).</summary>
    public ModrinthSearchQuery At(string searchUrl) => new(root, searchUrl);
}
