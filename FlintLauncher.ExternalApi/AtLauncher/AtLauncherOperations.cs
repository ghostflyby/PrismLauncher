// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

namespace FlintLauncher.ExternalApi.AtLauncher;

// ------------------------------------------------------------------------------ packs

/// <summary>GET {download server}launcher/json/packsnew.json — the pack index.</summary>
public readonly record struct AtLauncherPackIndexQuery(AtLauncherClientOptions Options)
    : IApiOperation<List<IndexedPack>>
{
    public HttpRequestMessage CreateRequest() =>
        new(HttpMethod.Get, Options.DownloadServerUrl + "launcher/json/packsnew.json");

    public Exception WrapFailure(Exception failure) => new AtLauncherApiException(failure.Message, failure);

    public async Task<List<IndexedPack>> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await response.EnsureSuccessAsync(ct).ConfigureAwait(false);
        return AtLauncherJson.LoadPackIndex(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }
}

/// <summary>GET {download server}packs/{safeName}/versions/{version}/Configs.json — the pack
/// version manifest, normalized (see <see cref="AtLauncherJson.LoadPackVersion"/>).</summary>
public readonly record struct AtLauncherPackVersionQuery(
    AtLauncherClientOptions Options,
    string PackSafeName,
    string VersionName) : IApiOperation<PackVersion>
{
    public HttpRequestMessage CreateRequest() =>
        new(HttpMethod.Get,
            $"{Options.DownloadServerUrl}packs/{PackSafeName}/versions/{VersionName}/Configs.json");

    public Exception WrapFailure(Exception failure) => new AtLauncherApiException(failure.Message, failure);

    public async Task<PackVersion> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await response.EnsureSuccessAsync(ct).ConfigureAwait(false);
        return AtLauncherJson.LoadPackVersion(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }
}

// ------------------------------------------------------------------------- share codes

/// <summary>GET {api}share-codes/{code} — the share code's optional-mod selection; an
/// API-side error reads as <see cref="ShareCodeResponse.Error"/> rather than a
/// failure.</summary>
public readonly record struct AtLauncherShareCodeQuery(AtLauncherClientOptions Options, string Code)
    : IApiOperation<ShareCodeResponse>
{
    public HttpRequestMessage CreateRequest() =>
        new(HttpMethod.Get, Options.ApiBaseUrl + "share-codes/" + Code);

    public Exception WrapFailure(Exception failure) => new AtLauncherApiException(failure.Message, failure);

    public async Task<ShareCodeResponse> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await response.EnsureSuccessAsync(ct).ConfigureAwait(false);
        return AtLauncherJson.LoadShareCodeResponse(
            await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }
}

// ------------------------------------------------------------------------------ groups

/// <summary>The pack endpoints of <see cref="AtLauncherClient"/>.</summary>
public readonly struct AtLauncherPackQueries(AtLauncherClient root)
{
    /// <summary>The packsnew.json index.</summary>
    public AtLauncherPackIndexQuery Index() => new(root.Options);

    /// <summary>The Configs.json manifest of one pack version.</summary>
    public AtLauncherPackVersionQuery Version(string packSafeName, string versionName) =>
        new(root.Options, packSafeName, versionName);
}

/// <summary>The share-code endpoint of <see cref="AtLauncherClient"/>.</summary>
public readonly struct AtLauncherShareCodeQueries(AtLauncherClient root)
{
    /// <summary>The selection a share code encodes.</summary>
    public AtLauncherShareCodeQuery ByCode(string code) => new(root.Options, code);
}
