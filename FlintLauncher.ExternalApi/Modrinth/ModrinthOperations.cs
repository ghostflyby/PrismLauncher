// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace FlintLauncher.ExternalApi.Modrinth;

/// <summary>Adds the Authorization header when the options carry a PAT.</summary>
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
    ModrinthClientOptions Options,
    string Hash,
    string Algorithm) : IApiOperation<ModrinthVersion>
{
    public HttpRequestMessage CreateRequest() =>
        new HttpRequestMessage(HttpMethod.Get, $"{Options.BaseUrl}/version_file/{Hash}?algorithm={Algorithm}")
            .WithAuthorization(Options);

    public Exception WrapFailure(Exception failure) => new ModrinthApiException(failure.Message, failure);

    public Task<ModrinthVersion> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct) =>
        ModrinthDecoding.ReadJsonAsync<ModrinthVersion>(response, ModrinthJsonContext.Default.ModrinthVersion, ct);
}

/// <summary>POST /version_files — the versions for a batch of file hashes; an empty hash
/// list or algorithm is omitted from the body.</summary>
public readonly record struct ModrinthFileVersionLookupQuery(
    ModrinthClientOptions Options,
    IReadOnlyList<string> Hashes,
    string Algorithm)
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
        ModrinthDecoding.ReadJsonAsync<Dictionary<string, ModrinthVersion>>(response,
            ModrinthJsonContext.Default.DictionaryStringModrinthVersion, ct);
}

/// <summary>POST /version_files/update — the latest versions for a batch of file hashes;
/// the optional lists shape the body and are omitted when empty (#3404).</summary>
public readonly record struct ModrinthLatestVersionsQuery(
    ModrinthClientOptions Options,
    IReadOnlyList<string> Hashes,
    string Algorithm,
    List<string>? Loaders,
    List<string>? GameVersions,
    List<string>? VersionTypes)
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
        ModrinthDecoding.ReadJsonAsync<Dictionary<string, ModrinthVersion>>(response,
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
        ModrinthDecoding.ReadJsonAsync<ModrinthVersion>(response, ModrinthJsonContext.Default.ModrinthVersion, ct);
}

/// <summary>GET /project/{id}/version — the version list behind a project, filtered by
/// the wire's game-version and loader strings (omitted when empty); requests carry the
/// include_changelog=false default.</summary>
public readonly record struct ModrinthVersionListQuery(
    ModrinthClientOptions Options,
    string ProjectId,
    IReadOnlyList<string>? GameVersions = null,
    IReadOnlyList<string>? Loaders = null,
    bool IncludeChangelog = false)
    : IApiOperation<List<ModrinthVersion>>
{
    public HttpRequestMessage CreateRequest()
    {
        var query = "include_changelog=" + (IncludeChangelog ? "true" : "false");
        if (GameVersions is { Count: > 0 })
        {
            query = $"game_versions=[\"{string.Join("\",\"", GameVersions)}\"]&" + query;
        }

        if (Loaders is { Count: > 0 })
        {
            query = $"loaders=[\"{string.Join("\",\"", Loaders)}\"]&" + query;
        }

        return new HttpRequestMessage(HttpMethod.Get,
            $"{Options.BaseUrl}/project/{ProjectId}/version?{query}").WithAuthorization(Options);
    }

    public Exception WrapFailure(Exception failure) => new ModrinthApiException(failure.Message, failure);

    public Task<List<ModrinthVersion>> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct) =>
        ModrinthDecoding.ReadJsonAsync<List<ModrinthVersion>>(response, ModrinthJsonContext.Default.ListModrinthVersion, ct);
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
        ModrinthDecoding.ReadJsonAsync<ModrinthProject>(response, ModrinthJsonContext.Default.ModrinthProject, ct);
}

/// <summary>GET /projects?ids=[...] — the projects for a batch of ids.</summary>
public readonly record struct ModrinthProjectsQuery(ModrinthClientOptions Options, IReadOnlyList<string> Ids)
    : IApiOperation<List<ModrinthProject>>
{
    public HttpRequestMessage CreateRequest() => new HttpRequestMessage(HttpMethod.Get,
        Options.BaseUrl + $"/projects?ids=[\"{string.Join("\",\"", Ids)}\"]").WithAuthorization(Options);

    public Exception WrapFailure(Exception failure) => new ModrinthApiException(failure.Message, failure);

    public Task<List<ModrinthProject>> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct) =>
        ModrinthDecoding.ReadJsonAsync<List<ModrinthProject>>(response, ModrinthJsonContext.Default.ListModrinthProject, ct);
}

/// <summary>GET /project/{id}/description — the project body as plain text; a failed
/// status resolves to "".</summary>
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
        ModrinthDecoding.ReadJsonAsync<List<ModrinthCategory>>(response, ModrinthJsonContext.Default.ListModrinthCategory, ct);
}

/// <summary>GET /search — the search envelope: the page offset, the free text, the sort
/// index and the facet groups (each group OR-s its values, the groups AND together).</summary>
public readonly record struct ModrinthSearchQuery(
    ModrinthClientOptions Options,
    int Offset,
    string? Query,
    string? Index,
    string ProjectType,
    IReadOnlyList<IReadOnlyList<string>> Facets)
    : IApiOperation<ModrinthSearchResults>
{
    public HttpRequestMessage CreateRequest()
    {
        var getArguments = new List<string>
        {
            $"offset={Offset}",
            "limit=25",
        };
        if (Query is { Length: > 0 })
        {
            getArguments.Add($"query={Uri.EscapeDataString(Query)}");
        }

        if (Index is { Length: > 0 })
        {
            getArguments.Add($"index={Index}");
        }

        var facets = Facets.Select(group => group.ToList()).ToList();
        getArguments.Add($"facets={Uri.EscapeDataString(
            JsonSerializer.Serialize(facets, ModrinthJsonContext.Default.ListListString))}");

        return new HttpRequestMessage(HttpMethod.Get,
            $"{Options.BaseUrl}/search?{string.Join("&", getArguments)}").WithAuthorization(Options);
    }

    public Exception WrapFailure(Exception failure) => new ModrinthApiException(failure.Message, failure);

    public Task<ModrinthSearchResults> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct) =>
        ModrinthDecoding.ReadJsonAsync<ModrinthSearchResults>(response, ModrinthJsonContext.Default.ModrinthSearchResults, ct);
}

// ----------------------------------------------------------------------------- downloads

/// <summary>GET a download URL and commit it to the destination file: the body lands in a
/// sibling temp file that replaces the destination only after the checksum matched (a null
/// checksum or an unknown algorithm downloads unchecked). Send with the streaming read
/// option (ResponseHeadersRead); the destination path is the send argument and the result
/// is the number of bytes written.</summary>
public readonly record struct ModrinthFileDownload(
    ModrinthClientOptions Options,
    string Url,
    ModrinthDownloadMeta? Meta = null,
    (string Algorithm, string Hash)? Checksum = null)
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
/// version-list fetch.</summary>
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

    /// <summary>GET /project/{id}/version — the project's version list, filtered by the
    /// wire's game-version/loader strings.</summary>
    public ModrinthVersionListQuery Query(string projectId, IReadOnlyList<string>? gameVersions = null,
        IReadOnlyList<string>? loaders = null, bool includeChangelog = false) =>
        new(root.Options, projectId, gameVersions, loaders, includeChangelog);
}

/// <summary>The project endpoints: single and batch project fetches plus the description
/// body.</summary>
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
    /// <summary>GET /search — the wire search: page offset, free text, sort index,
    /// project_type facet and the facet OR-groups.</summary>
    public ModrinthSearchQuery Query(int offset, string? query, string? index, string projectType,
        IReadOnlyList<IReadOnlyList<string>> facets) =>
        new(root.Options, offset, query, index, projectType, facets);
}

/// <summary>The download endpoints: CDN files streamed to disk.</summary>
public readonly struct ModrinthDownloadQueries(ModrinthClient root)
{
    /// <summary>GET a download URL into a destination file (checksum-gated temp-file
    /// commit); resolves to the bytes written.</summary>
    public ModrinthFileDownload File(string url,
        ModrinthDownloadMeta? meta = null, (string Algorithm, string Hash)? checksum = null) =>
        new(root.Options, url, meta, checksum);
}
