// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// The Modrinth v2 endpoints as operation values: each endpoint is a struct bound to the
// client (the navigation root whose Options carry the base URL) plus its arguments, and
// knows how to build its request and read its response. The client exposes them through
// per-area groups (Versions / Projects / Tags / Search / Downloads) whose methods return
// the values; sending goes through the shared ApiOperation pipeline. The JSON readers
// decode strictly (RespectNullableAnnotations) straight from the response stream through
// the serializer's stream API and share the status gate; the description endpoint reads
// plain text and the download streams to disk, each with its own status rule.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace FlintLauncher.ExternalApi;

/// <summary>The shared decode of the JSON operations: the success gate, then strict
/// source-generated streaming decode — a JSON null document fails like any shape
/// mismatch.</summary>
internal static class ModrinthDecoding
{
    internal static async Task<T> ReadJsonAsync<T>(HttpRequestMessage request, HttpResponseMessage response,
        Stream body, JsonTypeInfo<T> typeInfo, CancellationToken ct) where T : class
    {
        await ApiOperation.EnsureSuccessAsync(request, response, body, ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(body, typeInfo, ct).ConfigureAwait(false)
               ?? throw new JsonException("the response body is null");
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

    public Task<ModrinthVersion> ReadResponseAsync(HttpRequestMessage request,
        HttpResponseMessage response, Stream body, CancellationToken ct) =>
        ModrinthDecoding.ReadJsonAsync(request, response, body,
            ModrinthJsonContext.Default.ModrinthVersion, ct);
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

    public Task<Dictionary<string, ModrinthVersion>> ReadResponseAsync(HttpRequestMessage request,
        HttpResponseMessage response, Stream body, CancellationToken ct) =>
        ModrinthDecoding.ReadJsonAsync(request, response, body,
            ModrinthJsonContext.Default.DictionaryStringModrinthVersion, ct);
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

    public Task<Dictionary<string, ModrinthVersion>> ReadResponseAsync(HttpRequestMessage request,
        HttpResponseMessage response, Stream body, CancellationToken ct) =>
        ModrinthDecoding.ReadJsonAsync(request, response, body,
            ModrinthJsonContext.Default.DictionaryStringModrinthVersion, ct);
}

/// <summary>GET /version/{id} — one pinned version (the dependency lookup).</summary>
public readonly record struct ModrinthVersionQuery(ModrinthClient Root, string VersionId)
    : IApiOperation<ModrinthVersion>
{
    public IApiExecutor Executor => Root;

    public HttpRequestMessage CreateRequest() =>
        new(HttpMethod.Get, $"{Root.Options.BaseUrl}/version/{VersionId}");

    public Task<ModrinthVersion> ReadResponseAsync(HttpRequestMessage request,
        HttpResponseMessage response, Stream body, CancellationToken ct) =>
        ModrinthDecoding.ReadJsonAsync(request, response, body,
            ModrinthJsonContext.Default.ModrinthVersion, ct);
}

/// <summary>GET a version-list URL — the array of versions behind a project's /version
/// endpoint; the query string is built by the caller (the Core adapter owns the
/// game-version/loader filters).</summary>
public readonly record struct ModrinthVersionListQuery(ModrinthClient Root, string VersionsUrl)
    : IApiOperation<List<ModrinthVersion>>
{
    public IApiExecutor Executor => Root;

    public HttpRequestMessage CreateRequest() => new(HttpMethod.Get, VersionsUrl);

    public Task<List<ModrinthVersion>> ReadResponseAsync(HttpRequestMessage request,
        HttpResponseMessage response, Stream body, CancellationToken ct) =>
        ModrinthDecoding.ReadJsonAsync(request, response, body,
            ModrinthJsonContext.Default.ListModrinthVersion, ct);
}

// ------------------------------------------------------------------------------ projects

/// <summary>GET /project/{id} — one project.</summary>
public readonly record struct ModrinthProjectQuery(ModrinthClient Root, string Id)
    : IApiOperation<ModrinthProject>
{
    public IApiExecutor Executor => Root;

    public HttpRequestMessage CreateRequest() =>
        new(HttpMethod.Get, $"{Root.Options.BaseUrl}/project/{Id}");

    public Task<ModrinthProject> ReadResponseAsync(HttpRequestMessage request,
        HttpResponseMessage response, Stream body, CancellationToken ct) =>
        ModrinthDecoding.ReadJsonAsync(request, response, body,
            ModrinthJsonContext.Default.ModrinthProject, ct);
}

/// <summary>GET /projects?ids=[...] — the projects for a batch of ids.</summary>
public readonly record struct ModrinthProjectsQuery(ModrinthClient Root, IReadOnlyList<string> Ids)
    : IApiOperation<List<ModrinthProject>>
{
    public IApiExecutor Executor => Root;

    public HttpRequestMessage CreateRequest() => new(HttpMethod.Get,
        Root.Options.BaseUrl + $"/projects?ids=[\"{string.Join("\",\"", Ids)}\"]");

    public Task<List<ModrinthProject>> ReadResponseAsync(HttpRequestMessage request,
        HttpResponseMessage response, Stream body, CancellationToken ct) =>
        ModrinthDecoding.ReadJsonAsync(request, response, body,
            ModrinthJsonContext.Default.ListModrinthProject, ct);
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

    public async Task<string> ReadResponseAsync(HttpRequestMessage request,
        HttpResponseMessage response, Stream body, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            return "";
        }

        using var reader = new StreamReader(body);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }
}

// ------------------------------------------------------------------------ tags & search

/// <summary>GET /tag/category.</summary>
public readonly record struct ModrinthCategoriesQuery(ModrinthClient Root)
    : IApiOperation<List<ModrinthCategory>>
{
    public IApiExecutor Executor => Root;

    public HttpRequestMessage CreateRequest() =>
        new(HttpMethod.Get, $"{Root.Options.BaseUrl}/tag/category");

    public Task<List<ModrinthCategory>> ReadResponseAsync(HttpRequestMessage request,
        HttpResponseMessage response, Stream body, CancellationToken ct) =>
        ModrinthDecoding.ReadJsonAsync(request, response, body,
            ModrinthJsonContext.Default.ListModrinthCategory, ct);
}

/// <summary>GET /search — the search envelope; the query string is built by the caller
/// (the Core adapter owns the facet building).</summary>
public readonly record struct ModrinthSearchQuery(ModrinthClient Root, string SearchUrl)
    : IApiOperation<ModrinthSearchResults>
{
    public IApiExecutor Executor => Root;

    public HttpRequestMessage CreateRequest() => new(HttpMethod.Get, SearchUrl);

    public Task<ModrinthSearchResults> ReadResponseAsync(HttpRequestMessage request,
        HttpResponseMessage response, Stream body, CancellationToken ct) =>
        ModrinthDecoding.ReadJsonAsync(request, response, body,
            ModrinthJsonContext.Default.ModrinthSearchResults, ct);
}

// ----------------------------------------------------------------------------- downloads

/// <summary>GET a download URL: the body streams into a sibling temp file and the file is
/// moved into place only after the checksum matched (a null checksum or an unknown
/// algorithm downloads unchecked), so a failed attempt never leaves a half-written
/// artifact. The download-meta header rides on the request when the meta carries content.
/// Resolves to the number of bytes written.</summary>
public readonly record struct ModrinthFileDownload(ModrinthClient Root, string Url, string DestinationPath,
    ModrinthDownloadMeta? Meta = null, (string Algorithm, string Hash)? Checksum = null) : IApiOperation<long>
{
    public IApiExecutor Executor => Root;

    public HttpRequestMessage CreateRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Url);
        if (Meta is { } downloadMeta && !downloadMeta.IsEmpty)
        {
            request.Headers.TryAddWithoutValidation("modrinth-download-meta",
                JsonSerializer.Serialize(downloadMeta, ModrinthJsonContext.Default.ModrinthDownloadMeta));
        }

        return request;
    }

    public async Task<long> ReadResponseAsync(HttpRequestMessage request,
        HttpResponseMessage response, Stream body, CancellationToken ct)
    {
        await ApiOperation.EnsureSuccessAsync(request, response, body, ct).ConfigureAwait(false);

        var tempPath = DestinationPath + ".download";
        try
        {
            long written = 0;
            await using (var output = File.Create(tempPath))
            {
                using var hasher = Checksum is { } expected && AlgorithmName(expected.Algorithm) is { } name
                    ? IncrementalHash.CreateHash(name)
                    : null;
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    hasher?.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    written += read;
                }

                if (hasher is not null)
                {
                    var expectedHash = Checksum!.Value;
                    var actual = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
                    if (!string.Equals(actual, expectedHash.Hash, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            $"checksum mismatch for {request.RequestUri}: expected {expectedHash.Algorithm} {expectedHash.Hash}, got {actual}");
                    }
                }
            }

            File.Move(tempPath, DestinationPath, overwrite: true);
            return written;
        }
        catch
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (IOException)
            {
                // the temp file best-effort cleanup must not mask the real failure
            }

            throw;
        }
    }

    /// <summary>The wire checksum names this endpoint accepts; anything else (murmur2,
    /// unknown) downloads unchecked.</summary>
    private static HashAlgorithmName? AlgorithmName(string modrinthAlgorithm) => modrinthAlgorithm switch
    {
        "sha1" => HashAlgorithmName.SHA1,
        "sha256" => HashAlgorithmName.SHA256,
        "sha512" => HashAlgorithmName.SHA512,
        "md5" => HashAlgorithmName.MD5,
        _ => null,
    };
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

/// <summary>The download endpoints: CDN files streamed to disk.</summary>
public readonly struct ModrinthDownloadQueries(ModrinthClient root)
{
    /// <summary>GET a download URL into the destination file (checksum-gated temp-file
    /// commit; resolves to the bytes written).</summary>
    public ModrinthFileDownload File(string url, string destinationPath,
        ModrinthDownloadMeta? meta = null, (string Algorithm, string Hash)? checksum = null) =>
        new(root, url, destinationPath, meta, checksum);
}
