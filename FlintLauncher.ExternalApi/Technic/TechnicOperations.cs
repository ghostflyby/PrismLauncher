// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

namespace FlintLauncher.ExternalApi.Technic;

// ----------------------------------------------------------------------------- platform

/// <summary>The search of ListModel::performSearch: an empty term reads the trending list, a
/// modpack API URL (or "#slug") reads that single pack, anything else searches. The
/// single-pack lookups address api.technicpack.net directly, like the C++ model.</summary>
public readonly record struct TechnicSearchQuery(TechnicClientOptions Options, string Term)
    : IApiOperation<TechnicSearchResponse>
{
    /// <summary>The request URL the term selects.</summary>
    public string Url => Target().Url;

    /// <summary>The response shape the term selects.</summary>
    public TechnicSearchMode Mode => Target().Mode;

    public HttpRequestMessage CreateRequest() => new(HttpMethod.Get, Url);

    public Exception WrapFailure(Exception failure) => new TechnicApiException(failure.Message, failure);

    public async Task<TechnicSearchResponse> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await response.EnsureSuccessAsync(ct).ConfigureAwait(false);
        return TechnicJson.LoadSearch(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false), Mode);
    }

    private (string Url, TechnicSearchMode Mode) Target()
    {
        var build = Options.ApiBuild;
        if (Term.Length == 0)
        {
            return ($"{Options.BaseUrl}trending?build={build}", TechnicSearchMode.List);
        }

        if (Term.StartsWith("http://api.technicpack.net/modpack/", StringComparison.Ordinal))
        {
            return ($"https://{Term[7..]}?build={build}", TechnicSearchMode.Single);
        }

        if (Term.StartsWith("https://api.technicpack.net/modpack/", StringComparison.Ordinal))
        {
            return ($"{Term}?build={build}", TechnicSearchMode.Single);
        }

        if (Term.StartsWith("#", StringComparison.Ordinal))
        {
            return ($"https://api.technicpack.net/modpack/{Term[1..]}?build={build}", TechnicSearchMode.Single);
        }

        return ($"{Options.BaseUrl}search?build={build}&q={Uri.EscapeDataString(Term)}", TechnicSearchMode.List);
    }
}

/// <summary>GET {base}modpack/{slug}?build= — the pack metadata (TechnicPage's metadata
/// step); a malformed document reads as null.</summary>
public readonly record struct TechnicPackMetadataQuery(TechnicClientOptions Options, string Slug)
    : IApiOperation<TechnicMetadataJson?>
{
    public HttpRequestMessage CreateRequest() =>
        new(HttpMethod.Get, $"{Options.BaseUrl}modpack/{Slug}?build={Options.ApiBuild}");

    public Exception WrapFailure(Exception failure) => new TechnicApiException(failure.Message, failure);

    public async Task<TechnicMetadataJson?> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await response.EnsureSuccessAsync(ct).ConfigureAwait(false);
        return TechnicJson.LoadMetadata(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }
}

// ------------------------------------------------------------------------------ solder

/// <summary>GET {solder}/modpack/{slug} — the Solder build listing (TechnicPage::
/// metadataLoaded); a malformed document reads as null.</summary>
public readonly record struct TechnicSolderPackQuery(string SolderUrl, string Slug)
    : IApiOperation<SolderPack?>
{
    public HttpRequestMessage CreateRequest() => new(HttpMethod.Get, $"{SolderUrl}/modpack/{Slug}");

    public Exception WrapFailure(Exception failure) => new TechnicApiException(failure.Message, failure);

    public async Task<SolderPack?> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await response.EnsureSuccessAsync(ct).ConfigureAwait(false);
        return TechnicJson.LoadSolderPack(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }
}

/// <summary>GET {solder}/modpack/{slug}/{build} — one Solder build (SolderPackInstallTask);
/// the decode is strict (see <see cref="TechnicJson.LoadSolderPackBuild"/>).</summary>
public readonly record struct TechnicSolderBuildQuery(string SolderUrl, string Slug, string Build)
    : IApiOperation<SolderPackBuild>
{
    public HttpRequestMessage CreateRequest() =>
        new(HttpMethod.Get, $"{SolderUrl}/modpack/{Slug}/{Build}");

    public Exception WrapFailure(Exception failure) => new TechnicApiException(failure.Message, failure);

    public async Task<SolderPackBuild> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await response.EnsureSuccessAsync(ct).ConfigureAwait(false);
        return TechnicJson.LoadSolderPackBuild(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }
}

// ------------------------------------------------------------------------------ groups

/// <summary>The search endpoint of <see cref="TechnicClient"/>.</summary>
public readonly struct TechnicSearchQueries(TechnicClient root)
{
    /// <summary>The search a term selects (empty = trending).</summary>
    public TechnicSearchQuery ForTerm(string term) => new(root.Options, term);
}

/// <summary>The pack endpoints of <see cref="TechnicClient"/>.</summary>
public readonly struct TechnicPackQueries(TechnicClient root)
{
    /// <summary>The metadata of one pack slug.</summary>
    public TechnicPackMetadataQuery Metadata(string slug) => new(root.Options, slug);
}

/// <summary>The Solder endpoints of a pack's Solder instance.</summary>
public readonly struct TechnicSolderQueries
{
    /// <summary>The build listing of a pack on a Solder instance.</summary>
    public TechnicSolderPackQuery Pack(string solderUrl, string slug) => new(solderUrl, slug);

    /// <summary>One build of a pack on a Solder instance.</summary>
    public TechnicSolderBuildQuery Build(string solderUrl, string slug, string build) =>
        new(solderUrl, slug, build);
}
