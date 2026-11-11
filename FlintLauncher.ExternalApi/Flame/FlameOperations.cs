// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace FlintLauncher.ExternalApi.Flame;

/// <summary>Adds the x-api-key header when the options carry a key.</summary>
internal static class FlameRequests
{
    internal static HttpRequestMessage WithApiKey(this HttpRequestMessage request,
        FlameClientOptions options)
    {
        if (options.ApiKey.Length != 0)
        {
            request.Headers.TryAddWithoutValidation("x-api-key", options.ApiKey);
        }

        return request;
    }
}

/// <summary>The strict source-generated streaming decode of the JSON operations — a JSON
/// null document fails like any shape mismatch. Status handling stays with each
/// operation.</summary>
internal static class FlameDecoding
{
    internal static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response,
        JsonTypeInfo<T> typeInfo, CancellationToken ct) where T : class
    {
        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(body, typeInfo, ct).ConfigureAwait(false)
               ?? throw new JsonException("the response body is null");
    }
}

// ------------------------------------------------------------------------------ projects

/// <summary>GET /mods/{id} — one project.</summary>
public readonly record struct FlameProjectQuery(FlameClientOptions Options, string ProjectId)
    : IApiOperation<FlameProjectEnvelope>
{
    public HttpRequestMessage CreateRequest() =>
        new HttpRequestMessage(HttpMethod.Get, $"{Options.BaseUrl}/mods/{ProjectId}")
            .WithApiKey(Options);

    public Exception WrapFailure(Exception failure) => new FlameApiException(failure.Message, failure);

    public async Task<FlameProjectEnvelope> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await response.EnsureSuccessAsync(ct).ConfigureAwait(false);
        return await FlameDecoding.ReadJsonAsync(response, FlameJsonContext.Default.FlameProjectEnvelope, ct)
            .ConfigureAwait(false);
    }
}

/// <summary>POST /mods — the projects for a batch of ids (the decimal-string body
/// quirk).</summary>
public readonly record struct FlameProjectsQuery(FlameClientOptions Options, IReadOnlyList<string> ProjectIds)
    : IApiOperation<FlameProjectList>
{
    public HttpRequestMessage CreateRequest()
    {
        var body = new FlameModsRequest([.. ProjectIds]);
        return new HttpRequestMessage(HttpMethod.Post, $"{Options.BaseUrl}/mods")
        {
            Content = new ByteArrayContent(
                JsonSerializer.SerializeToUtf8Bytes(body, FlameJsonContext.Default.FlameModsRequest)),
        }.WithApiKey(Options);
    }

    public Exception WrapFailure(Exception failure) => new FlameApiException(failure.Message, failure);

    public async Task<FlameProjectList> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await response.EnsureSuccessAsync(ct).ConfigureAwait(false);
        return await FlameDecoding.ReadJsonAsync(response, FlameJsonContext.Default.FlameProjectList, ct)
            .ConfigureAwait(false);
    }
}

/// <summary>GET /mods/{id}/description — the project body as the {"data": string} envelope;
/// a failed status resolves to "".</summary>
public readonly record struct FlameProjectDescriptionQuery(FlameClientOptions Options, int ModId)
    : IApiOperation<string>
{
    public HttpRequestMessage CreateRequest() =>
        new HttpRequestMessage(HttpMethod.Get, $"{Options.BaseUrl}/mods/{ModId}/description")
            .WithApiKey(Options);

    public Exception WrapFailure(Exception failure) => new FlameApiException(failure.Message, failure);

    public async Task<string> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            return "";
        }

        var envelope = await FlameDecoding.ReadJsonAsync(response,
            FlameJsonContext.Default.FlameStringEnvelope, ct).ConfigureAwait(false);
        return envelope.Data ?? "";
    }
}

// -------------------------------------------------------------------------------- files

/// <summary>GET /mods/{projectId}/files/{fileId} — one pinned file.</summary>
public readonly record struct FlameFileQuery(FlameClientOptions Options, string ProjectId, string FileId)
    : IApiOperation<FlameFileEnvelope>
{
    public HttpRequestMessage CreateRequest() =>
        new HttpRequestMessage(HttpMethod.Get, $"{Options.BaseUrl}/mods/{ProjectId}/files/{FileId}")
            .WithApiKey(Options);

    public Exception WrapFailure(Exception failure) => new FlameApiException(failure.Message, failure);

    public async Task<FlameFileEnvelope> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await response.EnsureSuccessAsync(ct).ConfigureAwait(false);
        return await FlameDecoding.ReadJsonAsync(response, FlameJsonContext.Default.FlameFileEnvelope, ct)
            .ConfigureAwait(false);
    }
}

/// <summary>POST /mods/files — the files for a batch of ids (the decimal-string body
/// quirk).</summary>
public readonly record struct FlameFilesQuery(FlameClientOptions Options, IReadOnlyList<string> FileIds)
    : IApiOperation<FlameFileList>
{
    public HttpRequestMessage CreateRequest()
    {
        var body = new FlameFilesRequest([.. FileIds]);
        return new HttpRequestMessage(HttpMethod.Post, $"{Options.BaseUrl}/mods/files")
        {
            Content = new ByteArrayContent(
                JsonSerializer.SerializeToUtf8Bytes(body, FlameJsonContext.Default.FlameFilesRequest)),
        }.WithApiKey(Options);
    }

    public Exception WrapFailure(Exception failure) => new FlameApiException(failure.Message, failure);

    public async Task<FlameFileList> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await response.EnsureSuccessAsync(ct).ConfigureAwait(false);
        return await FlameDecoding.ReadJsonAsync(response, FlameJsonContext.Default.FlameFileList, ct)
            .ConfigureAwait(false);
    }
}

/// <summary>GET /mods/{projectId}/files — the project's file list, filtered by the wire's
/// game-version string and loader id (both omitted when null).</summary>
public readonly record struct FlameFileListQuery(
    FlameClientOptions Options,
    string ProjectId,
    string? GameVersion = null,
    int? ModLoaderType = null)
    : IApiOperation<FlameFileList>
{
    public HttpRequestMessage CreateRequest()
    {
        var query = "pageSize=10000";
        if (GameVersion is { Length: > 0 } gameVersion)
        {
            query += $"&gameVersion={gameVersion}";
        }

        if (ModLoaderType is { } loaderType)
        {
            query += $"&modLoaderType={loaderType}";
        }

        return new HttpRequestMessage(HttpMethod.Get, $"{Options.BaseUrl}/mods/{ProjectId}/files?{query}")
            .WithApiKey(Options);
    }

    public Exception WrapFailure(Exception failure) => new FlameApiException(failure.Message, failure);

    public async Task<FlameFileList> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await response.EnsureSuccessAsync(ct).ConfigureAwait(false);
        return await FlameDecoding.ReadJsonAsync(response, FlameJsonContext.Default.FlameFileList, ct)
            .ConfigureAwait(false);
    }
}

/// <summary>GET /mods/{modId}/files/{fileId}/changelog — the changelog as the
/// {"data": string} envelope; a failed status resolves to "".</summary>
public readonly record struct FlameFileChangelogQuery(FlameClientOptions Options, int ModId, int FileId)
    : IApiOperation<string>
{
    public HttpRequestMessage CreateRequest() =>
        new HttpRequestMessage(HttpMethod.Get, $"{Options.BaseUrl}/mods/{ModId}/files/{FileId}/changelog")
            .WithApiKey(Options);

    public Exception WrapFailure(Exception failure) => new FlameApiException(failure.Message, failure);

    public async Task<string> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            return "";
        }

        var envelope = await FlameDecoding.ReadJsonAsync(response,
            FlameJsonContext.Default.FlameStringEnvelope, ct).ConfigureAwait(false);
        return envelope.Data ?? "";
    }
}

// --------------------------------------------------------------------------- fingerprints

/// <summary>POST /fingerprints — the murmur2 batch match (the fingerprints serialize as
/// decimal strings, the C++ compatibility quirk).</summary>
public readonly record struct FlameFingerprintsQuery(FlameClientOptions Options, IReadOnlyList<uint> Fingerprints)
    : IApiOperation<FlameFingerprintResponse>
{
    public HttpRequestMessage CreateRequest()
    {
        var body = new FlameFingerprintsRequest(
            [.. Fingerprints.Select(fp => fp.ToString(CultureInfo.InvariantCulture))]);
        return new HttpRequestMessage(HttpMethod.Post, $"{Options.BaseUrl}/fingerprints")
        {
            Content = new ByteArrayContent(
                JsonSerializer.SerializeToUtf8Bytes(body, FlameJsonContext.Default.FlameFingerprintsRequest)),
        }.WithApiKey(Options);
    }

    public Exception WrapFailure(Exception failure) => new FlameApiException(failure.Message, failure);

    public async Task<FlameFingerprintResponse> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await response.EnsureSuccessAsync(ct).ConfigureAwait(false);
        return await FlameDecoding.ReadJsonAsync(response, FlameJsonContext.Default.FlameFingerprintResponse, ct)
            .ConfigureAwait(false);
    }
}

// ---------------------------------------------------------------------------- categories

/// <summary>GET /categories?gameId=432&amp;classId= — the categories of one CurseForge
/// class.</summary>
public readonly record struct FlameCategoriesQuery(FlameClientOptions Options, int ClassId)
    : IApiOperation<FlameCategoryList>
{
    public HttpRequestMessage CreateRequest() =>
        new HttpRequestMessage(HttpMethod.Get, $"{Options.BaseUrl}/categories?gameId=432&classId={ClassId}")
            .WithApiKey(Options);

    public Exception WrapFailure(Exception failure) => new FlameApiException(failure.Message, failure);

    public async Task<FlameCategoryList> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await response.EnsureSuccessAsync(ct).ConfigureAwait(false);
        return await FlameDecoding.ReadJsonAsync(response, FlameJsonContext.Default.FlameCategoryList, ct)
            .ConfigureAwait(false);
    }
}

// --------------------------------------------------------------------------------- search

/// <summary>GET /mods/search?gameId=432&amp;... — the search pass-through: the class, the
/// page index, the free text, the sort field (CurseForge sorts descending), the loader-id
/// filter list, the category-id filter list and the game-version filter (the launcher's
/// vocabulary translates into these wire arguments; each list serializes as the bare
/// bracket form CurseForge expects).</summary>
public readonly record struct FlameSearchQuery(
    FlameClientOptions Options,
    int ClassId,
    int Offset,
    string? SearchFilter = null,
    int? SortField = null,
    IReadOnlyList<int>? ModLoaderTypes = null,
    IReadOnlyList<string>? CategoryIds = null,
    string? GameVersion = null)
    : IApiOperation<FlameProjectList>
{
    public HttpRequestMessage CreateRequest()
    {
        var getArguments = new List<string>
        {
            $"classId={ClassId}",
            $"index={Offset}",
            "pageSize=25",
        };
        if (SearchFilter is { Length: > 0 } searchFilter)
        {
            getArguments.Add($"searchFilter={Uri.EscapeDataString(searchFilter)}");
        }

        if (SortField is { } sortField)
        {
            getArguments.Add($"sortField={sortField}");
        }

        getArguments.Add("sortOrder=desc");
        if (ModLoaderTypes is { Count: > 0 } loaderTypes)
        {
            getArguments.Add($"modLoaderTypes=[{string.Join(",", loaderTypes)}]");
        }

        if (CategoryIds is { Count: > 0 } categoryIds)
        {
            getArguments.Add($"categoryIds=[{string.Join(",", categoryIds)}]");
        }

        if (GameVersion is { Length: > 0 } gameVersion)
        {
            getArguments.Add($"gameVersion={gameVersion}");
        }

        return new HttpRequestMessage(HttpMethod.Get,
            $"{Options.BaseUrl}/mods/search?gameId=432&{string.Join("&", getArguments)}").WithApiKey(Options);
    }

    public Exception WrapFailure(Exception failure) => new FlameApiException(failure.Message, failure);

    public async Task<FlameProjectList> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await response.EnsureSuccessAsync(ct).ConfigureAwait(false);
        return await FlameDecoding.ReadJsonAsync(response, FlameJsonContext.Default.FlameProjectList, ct)
            .ConfigureAwait(false);
    }
}

// -------------------------------------------------------------------------------- groups

/// <summary>The project endpoints: single and batch fetches plus the description.</summary>
public readonly struct FlameProjectQueries(FlameClient root)
{
    /// <summary>GET /mods/{id}.</summary>
    public FlameProjectQuery ById(string projectId) => new(root.Options, projectId);

    /// <summary>POST /mods.</summary>
    public FlameProjectsQuery ByIds(IReadOnlyList<string> projectIds) => new(root.Options, projectIds);

    /// <summary>GET /mods/{id}/description (a failed status degrades to "").</summary>
    public FlameProjectDescriptionQuery Description(int modId) => new(root.Options, modId);
}

/// <summary>The file endpoints: single and batch fetches, the per-project file list and the
/// changelog.</summary>
public readonly struct FlameFileQueries(FlameClient root)
{
    /// <summary>GET /mods/{projectId}/files/{fileId}.</summary>
    public FlameFileQuery ById(string projectId, string fileId) => new(root.Options, projectId, fileId);

    /// <summary>POST /mods/files.</summary>
    public FlameFilesQuery ByIds(IReadOnlyList<string> fileIds) => new(root.Options, fileIds);

    /// <summary>GET /mods/{projectId}/files — the project's file list, filtered by the
    /// wire's game-version string and loader id.</summary>
    public FlameFileListQuery List(string projectId, string? gameVersion = null, int? modLoaderType = null) =>
        new(root.Options, projectId, gameVersion, modLoaderType);

    /// <summary>GET /mods/{modId}/files/{fileId}/changelog (a failed status degrades to "").</summary>
    public FlameFileChangelogQuery Changelog(int modId, int fileId) => new(root.Options, modId, fileId);
}

/// <summary>The fingerprint endpoints.</summary>
public readonly struct FlameFingerprintQueries(FlameClient root)
{
    /// <summary>POST /fingerprints — the murmur2 batch match.</summary>
    public FlameFingerprintsQuery Match(IReadOnlyList<uint> fingerprints) => new(root.Options, fingerprints);
}

/// <summary>The category endpoints.</summary>
public readonly struct FlameCategoryQueries(FlameClient root)
{
    /// <summary>GET /categories?gameId=432&amp;classId=.</summary>
    public FlameCategoriesQuery ForClassId(int classId) => new(root.Options, classId);
}

/// <summary>The search endpoint.</summary>
public readonly struct FlameSearchQueries(FlameClient root)
{
    /// <summary>GET /mods/search — the wire search: the class, the page, the free text,
    /// the sort field and the filter lists.</summary>
    public FlameSearchQuery Query(int classId, int offset, string? searchFilter = null, int? sortField = null,
        IReadOnlyList<int>? modLoaderTypes = null, IReadOnlyList<string>? categoryIds = null,
        string? gameVersion = null) =>
        new(root.Options, classId, offset, searchFilter, sortField, modLoaderTypes, categoryIds, gameVersion);
}
