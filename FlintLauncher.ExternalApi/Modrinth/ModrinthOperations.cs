// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace FlintLauncher.ExternalApi;

/// <summary>The PAT rides on every request when a token is set (the C++ ApiHeaderProxy
/// header logic); the transport strategies stay on the executor's handler chain.</summary>
internal static class ModrinthRequests
{
    internal static HttpRequestMessage WithAuthorization(this HttpRequestMessage request,
        ModrinthClientOptions options)
    {
        if (options.ApiToken.Length != 0)
        {
            request.Headers.TryAddWithoutValidation("Authorization", options.ApiToken);
        }

        return request;
    }
}

/// <summary>The shared decode of the JSON operations: the success gate, then strict
/// source-generated streaming decode — a JSON null document fails like any shape
/// mismatch.</summary>
internal static class ModrinthDecoding
{
    internal static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response,
        JsonTypeInfo<T> typeInfo, CancellationToken ct) where T : class
    {
        await ApiOperation.EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(body, typeInfo, ct).ConfigureAwait(false)
               ?? throw new JsonException("the response body is null");
    }
}

// ------------------------------------------------------------------------------ versions

/// <summary>GET /version_file/{hash}?algorithm= — the version for one file hash.</summary>
public readonly record struct ModrinthFileVersionQuery(
    ModrinthClientOptions Options, string Hash, string Algorithm) : IApiOperation<ModrinthVersion>
{
    public HttpRequestMessage CreateRequest() =>
        new HttpRequestMessage(HttpMethod.Get, $"{Options.BaseUrl}/version_file/{Hash}?algorithm={Algorithm}")
            .WithAuthorization(Options);

    public Exception WrapFailure(Exception failure) => new ModrinthApiException(failure.Message, failure);

    public Task<ModrinthVersion> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct) =>
        ModrinthDecoding.ReadJsonAsync(response, ModrinthJsonContext.Default.ModrinthVersion, ct);
}

/// <summary>POST /version_files — the versions for a batch of file hashes; an empty hash
/// list or algorithm is omitted from the body.</summary>
public readonly record struct ModrinthFileVersionLookupQuery(
    ModrinthClientOptions Options, IReadOnlyList<string> Hashes, string Algorithm)
    : IApiOperation<Dictionary<string, ModrinthVersion>>
{
    public HttpRequestMessage CreateRequest()
    {
        var body = new ModrinthHashLookupRequest(
            Hashes: Hashes.Count != 0 ? [.. Hashes] : null,
            Algorithm: Algorithm.Length != 0 ? Algorithm : null);
        return new HttpRequestMessage(HttpMethod.Post, $"{Options.BaseUrl}/version_files")
        {
            Content = new ByteArrayContent(
                JsonSerializer.SerializeToUtf8Bytes(body, ModrinthJsonContext.Default.ModrinthHashLookupRequest)),
        }.WithAuthorization(Options);
    }

    public Exception WrapFailure(Exception failure) => new ModrinthApiException(failure.Message, failure);

    public Task<Dictionary<string, ModrinthVersion>> ReadResponseAsync(HttpResponseMessage response,
        CancellationToken ct) =>
        ModrinthDecoding.ReadJsonAsync(response,
            ModrinthJsonContext.Default.DictionaryStringModrinthVersion, ct);
}

/// <summary>POST /version_files/update — the latest versions for a batch of file hashes;
/// the optional lists shape the body and are omitted when empty (#3404).</summary>
public readonly record struct ModrinthLatestVersionsQuery(
    ModrinthClientOptions Options, IReadOnlyList<string> Hashes, string Algorithm,
    List<string>? Loaders, List<string>? GameVersions, List<string>? VersionTypes)
    : IApiOperation<Dictionary<string, ModrinthVersion>>
{
    public HttpRequestMessage CreateRequest()
    {
        var body = new ModrinthVersionUpdateRequest(
            Hashes: Hashes is { Count: > 0 } ? [.. Hashes] : null,
            Algorithm: Algorithm is { Length: > 0 } ? Algorithm : null,
            Loaders: Loaders,
            GameVersions: GameVersions,
            VersionTypes: VersionTypes);
        return new HttpRequestMessage(HttpMethod.Post, $"{Options.BaseUrl}/version_files/update")
        {
            Content = new ByteArrayContent(
                JsonSerializer.SerializeToUtf8Bytes(body, ModrinthJsonContext.Default.ModrinthVersionUpdateRequest)),
        }.WithAuthorization(Options);
    }

    public Exception WrapFailure(Exception failure) => new ModrinthApiException(failure.Message, failure);

    public Task<Dictionary<string, ModrinthVersion>> ReadResponseAsync(HttpResponseMessage response,
        CancellationToken ct) =>
        ModrinthDecoding.ReadJsonAsync(response,
            ModrinthJsonContext.Default.DictionaryStringModrinthVersion, ct);
}

/// <summary>GET /version/{id} — one pinned version (the dependency lookup).</summary>
public readonly record struct ModrinthVersionQuery(ModrinthClientOptions Options, string VersionId)
    : IApiOperation<ModrinthVersion>
{
    public HttpRequestMessage CreateRequest() =>
        new HttpRequestMessage(HttpMethod.Get, $"{Options.BaseUrl}/version/{VersionId}")
            .WithAuthorization(Options);

    public Exception WrapFailure(Exception failure) => new ModrinthApiException(failure.Message, failure);

    public Task<ModrinthVersion> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct) =>
        ModrinthDecoding.ReadJsonAsync(response, ModrinthJsonContext.Default.ModrinthVersion, ct);
}

/// <summary>GET a version-list URL — the array of versions behind a project's /version
/// endpoint; the query string is built by the caller (the Core adapter owns the
/// game-version/loader filters).</summary>
public readonly record struct ModrinthVersionListQuery(ModrinthClientOptions Options, string VersionsUrl)
    : IApiOperation<List<ModrinthVersion>>
{
    public HttpRequestMessage CreateRequest() =>
        new HttpRequestMessage(HttpMethod.Get, VersionsUrl).WithAuthorization(Options);

    public Exception WrapFailure(Exception failure) => new ModrinthApiException(failure.Message, failure);

    public Task<List<ModrinthVersion>> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct) =>
        ModrinthDecoding.ReadJsonAsync(response, ModrinthJsonContext.Default.ListModrinthVersion, ct);
}

// ------------------------------------------------------------------------------ projects

/// <summary>GET /project/{id} — one project.</summary>
public readonly record struct ModrinthProjectQuery(ModrinthClientOptions Options, string Id)
    : IApiOperation<ModrinthProject>
{
    public HttpRequestMessage CreateRequest() =>
        new HttpRequestMessage(HttpMethod.Get, $"{Options.BaseUrl}/project/{Id}")
            .WithAuthorization(Options);

    public Exception WrapFailure(Exception failure) => new ModrinthApiException(failure.Message, failure);

    public Task<ModrinthProject> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct) =>
        ModrinthDecoding.ReadJsonAsync(response, ModrinthJsonContext.Default.ModrinthProject, ct);
}

/// <summary>GET /projects?ids=[...] — the projects for a batch of ids.</summary>
public readonly record struct ModrinthProjectsQuery(ModrinthClientOptions Options, IReadOnlyList<string> Ids)
    : IApiOperation<List<ModrinthProject>>
{
    public HttpRequestMessage CreateRequest() => new HttpRequestMessage(HttpMethod.Get,
        Options.BaseUrl + $"/projects?ids=[\"{string.Join("\",\"", Ids)}\"]").WithAuthorization(Options);

    public Exception WrapFailure(Exception failure) => new ModrinthApiException(failure.Message, failure);

    public Task<List<ModrinthProject>> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct) =>
        ModrinthDecoding.ReadJsonAsync(response, ModrinthJsonContext.Default.ListModrinthProject, ct);
}

/// <summary>GET /project/{id}/description — the project body as plain text. The endpoint
/// owns its status rule: any failed status degrades to "" (Modrinth has no per-file
/// changelog endpoint — changelogs ride on the version objects).</summary>
public readonly record struct ModrinthDescriptionQuery(ModrinthClientOptions Options, string Id)
    : IApiOperation<string>
{
    public HttpRequestMessage CreateRequest() =>
        new HttpRequestMessage(HttpMethod.Get, $"{Options.BaseUrl}/project/{Id}/description")
            .WithAuthorization(Options);

    public Exception WrapFailure(Exception failure) => new ModrinthApiException(failure.Message, failure);

    public async Task<string> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            return "";
        }

        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(body);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }
}

// ------------------------------------------------------------------------ tags & search

/// <summary>GET /tag/category.</summary>
public readonly record struct ModrinthCategoriesQuery(ModrinthClientOptions Options)
    : IApiOperation<List<ModrinthCategory>>
{
    public HttpRequestMessage CreateRequest() =>
        new HttpRequestMessage(HttpMethod.Get, $"{Options.BaseUrl}/tag/category")
            .WithAuthorization(Options);

    public Exception WrapFailure(Exception failure) => new ModrinthApiException(failure.Message, failure);

    public Task<List<ModrinthCategory>> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct) =>
        ModrinthDecoding.ReadJsonAsync(response, ModrinthJsonContext.Default.ListModrinthCategory, ct);
}

/// <summary>GET /search — the search envelope; the query string is built by the caller
/// (the Core adapter owns the facet building).</summary>
public readonly record struct ModrinthSearchQuery(ModrinthClientOptions Options, string SearchUrl)
    : IApiOperation<ModrinthSearchResults>
{
    public HttpRequestMessage CreateRequest() =>
        new HttpRequestMessage(HttpMethod.Get, SearchUrl).WithAuthorization(Options);

    public Exception WrapFailure(Exception failure) => new ModrinthApiException(failure.Message, failure);

    public Task<ModrinthSearchResults> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct) =>
        ModrinthDecoding.ReadJsonAsync(response, ModrinthJsonContext.Default.ModrinthSearchResults, ct);
}

// ----------------------------------------------------------------------------- downloads

/// <summary>GET a download URL: the body streams into a sibling temp file next to the
/// destination and the file is moved into place only after the checksum matched (a null
/// checksum or an unknown algorithm downloads unchecked), so a failed attempt never leaves
/// a half-written artifact. The download-meta header and the PAT ride on the request when
/// set; send it with the streaming read option (ResponseHeadersRead) so the body never
/// buffers whole. The destination file is not part of the request — it is the execution
/// argument of the send and resolves to the number of bytes written.</summary>
public readonly record struct ModrinthFileDownload(ModrinthClientOptions Options, string Url,
    ModrinthDownloadMeta? Meta = null, (string Algorithm, string Hash)? Checksum = null)
    : IApiOperation<string, long>
{
    public HttpRequestMessage CreateRequest(string destinationPath)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Url);
        if (Meta is { } downloadMeta && !downloadMeta.IsEmpty)
        {
            request.Headers.TryAddWithoutValidation("modrinth-download-meta",
                JsonSerializer.Serialize(downloadMeta, ModrinthJsonContext.Default.ModrinthDownloadMeta));
        }

        return request.WithAuthorization(Options);
    }

    public Exception WrapFailure(Exception failure) => new ModrinthApiException(failure.Message, failure);

    public async Task<long> ReadResponseAsync(string destinationPath, HttpResponseMessage response,
        CancellationToken ct)
    {
        await ApiOperation.EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        var tempPath = destinationPath + ".download";
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
                            $"checksum mismatch for {response.RequestMessage?.RequestUri}: expected {expectedHash.Algorithm} {expectedHash.Hash}, got {actual}");
                    }
                }
            }

            File.Move(tempPath, destinationPath, overwrite: true);
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
/// version-list fetch, each returning an operation value bound to the root's Options.</summary>
public readonly struct ModrinthVersionQueries(ModrinthClient root)
{
    /// <summary>GET /version_file/{hash}?algorithm=.</summary>
    public ModrinthFileVersionQuery ByFileHash(string hash, string algorithm) => new(root.Options, hash, algorithm);

    /// <summary>POST /version_files.</summary>
    public ModrinthFileVersionLookupQuery ByFileHashes(IReadOnlyList<string> hashes, string algorithm) =>
        new(root.Options, hashes, algorithm);

    /// <summary>POST /version_files/update.</summary>
    public ModrinthLatestVersionsQuery Latest(IReadOnlyList<string> hashes, string hashFormat,
        List<string>? loaders, List<string>? gameVersions, List<string>? versionTypes) =>
        new(root.Options, hashes, hashFormat, loaders, gameVersions, versionTypes);

    /// <summary>GET /version/{id}.</summary>
    public ModrinthVersionQuery ById(string versionId) => new(root.Options, versionId);

    /// <summary>GET a version-list URL (the Core adapter builds the query string).</summary>
    public ModrinthVersionListQuery At(string versionsUrl) => new(root.Options, versionsUrl);
}

/// <summary>The project endpoints: single and batch project fetches plus the description
/// body, each returning an operation value bound to the root's Options.</summary>
public readonly struct ModrinthProjectQueries(ModrinthClient root)
{
    /// <summary>GET /project/{id}.</summary>
    public ModrinthProjectQuery ById(string id) => new(root.Options, id);

    /// <summary>GET /projects?ids=[...].</summary>
    public ModrinthProjectsQuery ByIds(IReadOnlyList<string> ids) => new(root.Options, ids);

    /// <summary>GET /project/{id}/description (a failed status degrades to "").</summary>
    public ModrinthDescriptionQuery Description(string id) => new(root.Options, id);
}

/// <summary>The tag endpoints.</summary>
public readonly struct ModrinthTagQueries(ModrinthClient root)
{
    /// <summary>GET /tag/category.</summary>
    public ModrinthCategoriesQuery Categories() => new(root.Options);
}

/// <summary>The search endpoint.</summary>
public readonly struct ModrinthSearchQueries(ModrinthClient root)
{
    /// <summary>GET a search URL (the Core adapter builds the query string).</summary>
    public ModrinthSearchQuery At(string searchUrl) => new(root.Options, searchUrl);
}

/// <summary>The download endpoints: CDN files streamed to disk.</summary>
public readonly struct ModrinthDownloadQueries(ModrinthClient root)
{
    /// <summary>GET a download URL (checksum-gated temp-file commit); the destination file
    /// is the send's execution argument and the result is the bytes written.</summary>
    public ModrinthFileDownload File(string url,
        ModrinthDownloadMeta? meta = null, (string Algorithm, string Hash)? checksum = null) =>
        new(root.Options, url, meta, checksum);
}
