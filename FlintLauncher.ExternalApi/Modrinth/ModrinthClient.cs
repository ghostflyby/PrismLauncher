// SPDX-License-Identifier: GPL-3.0-only
// The Modrinth v2 client: queries return the wire models of ModrinthJson, decoded straight
// from the response stream by the source-generated serializer; file downloads land on disk
// through a checksum-verified temp-file dance. The client is a thin forwarder — one send
// per call, a success-status gate (non-2xx surfaces as HttpRequestException with the body
// excerpt in the message) and nothing else. Everything transport-shaped — proxy, user
// agent, timeouts, retries, resilience — belongs to the injected HttpClient and its handler
// pipeline, not here.

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace FlintLauncher.ExternalApi;

public sealed class ModrinthClient
{
    private readonly HttpClient _http;

    public ModrinthClient(HttpClient http, ModrinthClientOptions? options = null)
    {
        _http = http;
        Options = options ?? new();
    }

    /// <summary>The live configuration (hosts, PAT) this client reads per request; shared
    /// with whoever constructed it (the Core adapter forwards its properties here).</summary>
    public ModrinthClientOptions Options { get; }

    // ------------------------------------------------------------------ queries

    /// <summary>GET /version_file/{hash}?algorithm= — the version for one file hash.</summary>
    public Task<ModrinthVersion> GetCurrentVersionAsync(string hash, string hashFormat,
        CancellationToken cancellationToken = default) =>
        ReadJsonAsync(Get($"{Options.BaseUrl}/version_file/{hash}?algorithm={hashFormat}"),
            ModrinthJsonContext.Default.ModrinthVersion, cancellationToken);

    /// <summary>POST /version_files — the versions for a batch of file hashes.</summary>
    public Task<Dictionary<string, ModrinthVersion>> GetCurrentVersionsAsync(
        IReadOnlyList<string> hashes, string hashFormat, CancellationToken cancellationToken = default)
    {
        var body = new ModrinthHashLookupRequest(
            Hashes: hashes.Count != 0 ? [.. hashes] : null,
            Algorithm: hashFormat.Length != 0 ? hashFormat : null);
        return ReadJsonAsync(
            Post($"{Options.BaseUrl}/version_files", Serialize(body, ModrinthJsonContext.Default.ModrinthHashLookupRequest)),
            ModrinthJsonContext.Default.DictionaryStringModrinthVersion, cancellationToken);
    }

    /// <summary>POST /version_files/update — the latest versions for a batch of file hashes;
    /// the optional release types become the body's "version_types" list (#3404).</summary>
    public Task<Dictionary<string, ModrinthVersion>> GetLatestVersionsAsync(
        IReadOnlyList<string> hashes, string hashFormat,
        List<string>? loaders, List<string>? gameVersions, List<string>? versionTypes,
        CancellationToken cancellationToken = default) =>
        ReadJsonAsync(
            Post($"{Options.BaseUrl}/version_files/update",
                Serialize(UpdateBody(hashes, hashFormat, loaders, gameVersions, versionTypes),
                    ModrinthJsonContext.Default.ModrinthVersionUpdateRequest)),
            ModrinthJsonContext.Default.DictionaryStringModrinthVersion, cancellationToken);

    /// <summary>GET /projects?ids=[...] — the projects for a batch of ids.</summary>
    public Task<List<ModrinthProject>> GetProjectsAsync(IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default) =>
        ReadJsonAsync(Get(MultipleModInfoUrl(ids)),
            ModrinthJsonContext.Default.ListModrinthProject, cancellationToken);

    /// <summary>GET /project/{id} — one project.</summary>
    public Task<ModrinthProject> GetProjectAsync(string id, CancellationToken cancellationToken = default) =>
        ReadJsonAsync(Get($"{Options.BaseUrl}/project/{id}"),
            ModrinthJsonContext.Default.ModrinthProject, cancellationToken);

    /// <summary>GET /tag/category.</summary>
    public Task<List<ModrinthCategory>> GetCategoriesAsync(CancellationToken cancellationToken = default) =>
        ReadJsonAsync(Get($"{Options.BaseUrl}/tag/category"),
            ModrinthJsonContext.Default.ListModrinthCategory, cancellationToken);

    /// <summary>GET /version/{id} — one pinned version (the dependency lookup).</summary>
    public Task<ModrinthVersion> GetVersionAsync(string versionId, CancellationToken cancellationToken = default) =>
        ReadJsonAsync(Get($"{Options.BaseUrl}/version/{versionId}"),
            ModrinthJsonContext.Default.ModrinthVersion, cancellationToken);

    /// <summary>GET a version-list URL — the array of versions behind a project's
    /// /version endpoint; the query string is built by the caller (the Core adapter owns
    /// the game-version/loader filters).</summary>
    public Task<List<ModrinthVersion>> GetVersionListAsync(string versionsUrl,
        CancellationToken cancellationToken = default) =>
        ReadJsonAsync(Get(versionsUrl), ModrinthJsonContext.Default.ListModrinthVersion, cancellationToken);

    /// <summary>GET /search — the search envelope; the query string is built by the caller
    /// (the Core adapter owns the facet building).</summary>
    public Task<ModrinthSearchResults> GetSearchResultsAsync(string searchUrl,
        CancellationToken cancellationToken = default) =>
        ReadJsonAsync(Get(searchUrl), ModrinthJsonContext.Default.ModrinthSearchResults, cancellationToken);

    /// <summary>GET /project/{id}/description — the project body as plain text; "" when the
    /// request fails (Modrinth has no per-file changelog endpoint — changelogs ride on the
    /// version objects).</summary>
    public async Task<string> GetProjectDescriptionAsync(string projectId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await SendAsync(Get($"{Options.BaseUrl}/project/{projectId}/description"),
                cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
                : "";
        }
        catch (HttpRequestException)
        {
            return "";
        }
    }

    /// <summary>Stream a download URL to disk: the body flows into a sibling temp file and
    /// the file is moved into place only after the checksum matched ("" or null disables the
    /// check), so a failed attempt never leaves a half-written artifact. Transport failures,
    /// status failures and checksum mismatches all throw; a caller walking a fallback URL
    /// list just catches and moves on. The download-meta header rides on the request when
    /// the meta carries content.</summary>
    public async Task DownloadFileAsync(string url, string destinationPath,
        ModrinthDownloadMeta? meta = null, (string Algorithm, string Hash)? checksum = null,
        CancellationToken cancellationToken = default)
    {
        var request = Get(url);
        if (meta is { } downloadMeta && !downloadMeta.IsEmpty)
        {
            request.Headers.TryAddWithoutValidation("modrinth-download-meta",
                JsonSerializer.Serialize(downloadMeta, ModrinthJsonContext.Default.ModrinthDownloadMeta));
        }

        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            using var reader = new StreamReader(stream);
            var buffer = new char[4097];
            var read = await reader.ReadBlockAsync(buffer, cancellationToken).ConfigureAwait(false);
            var excerpt = new string(buffer, 0, Math.Min(read, 4096));
            throw new HttpRequestException(
                $"HTTP status {(int)response.StatusCode} for {request.RequestUri}\n{excerpt}",
                inner: null,
                statusCode: response.StatusCode);
        }

        var tempPath = destinationPath + ".download";
        try
        {
            await using (var output = File.Create(tempPath))
            {
                if (checksum is { } expected && AlgorithmName(expected.Algorithm) is { } algorithmName)
                {
                    using var hasher = IncrementalHash.CreateHash(algorithmName);
                    var buffer = new byte[64 * 1024];
                    int read;
                    while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        hasher.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    }

                    var actual = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
                    if (!string.Equals(actual, expected.Hash, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            $"checksum mismatch for {request.RequestUri}: expected {expected.Algorithm} {expected.Hash}, got {actual}");
                    }
                }
                else
                {
                    await stream.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                }
            }

            File.Move(tempPath, destinationPath, overwrite: true);
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

    // ------------------------------------------------------------------ plumbing

    private static HashAlgorithmName? AlgorithmName(string modrinthAlgorithm) => modrinthAlgorithm switch
    {
        "sha1" => HashAlgorithmName.SHA1,
        "sha256" => HashAlgorithmName.SHA256,
        "sha512" => HashAlgorithmName.SHA512,
        "md5" => HashAlgorithmName.MD5,
        _ => null,
    };

    private static HttpRequestMessage Get(string url) => new(HttpMethod.Get, url);

    private static HttpRequestMessage Post(string url, byte[] body) => new(HttpMethod.Post, url)
    {
        Content = new ByteArrayContent(body),
    };

    private static byte[] Serialize<T>(T body, JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.SerializeToUtf8Bytes(body, typeInfo);

    private static ModrinthVersionUpdateRequest UpdateBody(IReadOnlyList<string>? hashes, string? hashFormat,
        List<string>? loaders, List<string>? gameVersions, List<string>? versionTypes) => new(
        Hashes: hashes is { Count: > 0 } ? [.. hashes] : null,
        Algorithm: hashFormat is { Length: > 0 } ? hashFormat : null,
        Loaders: loaders,
        GameVersions: gameVersions,
        VersionTypes: versionTypes);

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken cancellationToken)
    {
        if (Options.ApiToken.Length != 0)
        {
            message.Headers.TryAddWithoutValidation("Authorization", Options.ApiToken);
        }

        return await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
    }

    private string MultipleModInfoUrl(IReadOnlyList<string> ids) =>
        Options.BaseUrl + $"/projects?ids=[\"{string.Join("\",\"", ids)}\"]";

    /// <summary>Send, gate on the success status (the first 4096 characters of the body ride
    /// in the exception message), then decode the body stream into the wire model. A JSON
    /// null document or a shape mismatch throws JsonException like any binding error.</summary>
    private async Task<T> ReadJsonAsync<T>(HttpRequestMessage message, JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken) where T : class
    {
        using var response = await SendAsync(message, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            using var reader = new StreamReader(stream);
            var buffer = new char[4097];
            var read = await reader.ReadBlockAsync(buffer, cancellationToken).ConfigureAwait(false);
            var excerpt = new string(buffer, 0, Math.Min(read, 4096));
            throw new HttpRequestException(
                $"HTTP status {(int)response.StatusCode} for {message.RequestUri}\n{excerpt}",
                inner: null,
                statusCode: response.StatusCode);
        }

        return await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken).ConfigureAwait(false)
               ?? throw new JsonException("the response body is null");
    }
}
