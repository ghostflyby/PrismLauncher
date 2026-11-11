// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

namespace FlintLauncher.ExternalApi.Ftb;

// ------------------------------------------------------------------------------ packs

/// <summary>GET /modpack/all — the whole-index pack id listing (a dud payload reads as an
/// empty list, see <see cref="FtbJson.LoadPackIndex"/>).</summary>
public readonly record struct FtbModpacksQuery(FtbClientOptions Options) : IApiOperation<List<int>>
{
    public HttpRequestMessage CreateRequest() =>
        new(HttpMethod.Get, $"{Options.BaseUrl}/modpack/all");

    public Exception WrapFailure(Exception failure) => new FtbApiException(failure.Message, failure);

    public async Task<List<int>> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await response.EnsureSuccessAsync(ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return FtbJson.LoadPackIndex(body);
    }
}

/// <summary>GET /modpack/{id} — the pack description.</summary>
public readonly record struct FtbModpackQuery(FtbClientOptions Options, int PackId)
    : IApiOperation<FtbModpack>
{
    public HttpRequestMessage CreateRequest() =>
        new(HttpMethod.Get, $"{Options.BaseUrl}/modpack/{PackId}");

    public Exception WrapFailure(Exception failure) => new FtbApiException(failure.Message, failure);

    public async Task<FtbModpack> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await response.EnsureSuccessAsync(ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return FtbJson.LoadModpack(body);
    }
}

/// <summary>GET /modpack/{id}/{versionId} — the version payload with targets[] and
/// files[].</summary>
public readonly record struct FtbVersionQuery(FtbClientOptions Options, int PackId, int VersionId)
    : IApiOperation<FtbVersion>
{
    public HttpRequestMessage CreateRequest() =>
        new(HttpMethod.Get, $"{Options.BaseUrl}/modpack/{PackId}/{VersionId}");

    public Exception WrapFailure(Exception failure) => new FtbApiException(failure.Message, failure);

    public async Task<FtbVersion> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await response.EnsureSuccessAsync(ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return FtbJson.LoadVersion(body);
    }
}

/// <summary>The pack endpoints of <see cref="FtbClient"/>.</summary>
public readonly struct FtbPackQueries(FtbClient root)
{
    /// <summary>The whole-index pack id listing.</summary>
    public FtbModpacksQuery All() => new(root.Options);

    /// <summary>The pack description of one id.</summary>
    public FtbModpackQuery ById(int packId) => new(root.Options, packId);

    /// <summary>The version payload of one pack version.</summary>
    public FtbVersionQuery Version(int packId, int versionId) => new(root.Options, packId, versionId);
}
