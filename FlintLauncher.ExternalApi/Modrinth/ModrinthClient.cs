// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// The Modrinth v2 client: the navigation root of the typed endpoint operations — the groups
// (Versions / Projects / Tags / Search / Downloads) return operation values bound to this
// root's live Options, and as the executor it is the pure transport they are sent through,
// reading the hosts and PAT from Options per send. The operations themselves own the read
// and decide the failure shape (ModrinthApiException, uniform across this root). The wire
// models and the strict decoding live in ModrinthJson; the download streams to disk
// through its operation's checksum-verified temp-file commit. Everything transport-shaped —
// proxy, user agent, timeouts, retries, resilience — belongs to the injected HttpClient and
// its handler pipeline, not here.

using System.Text.Json;

namespace FlintLauncher.ExternalApi;

public sealed class ModrinthClient : IApiExecutor
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

    // ------------------------------------------------------------------ endpoint groups

    /// <summary>The version endpoints: file-hash lookups, the batch update check and the
    /// version-list fetch.</summary>
    public ModrinthVersionQueries Versions => new(this);

    /// <summary>The project endpoints: single and batch fetches plus the description.</summary>
    public ModrinthProjectQueries Projects => new(this);

    /// <summary>The tag endpoints.</summary>
    public ModrinthTagQueries Tags => new(this);

    /// <summary>The search endpoint.</summary>
    public ModrinthSearchQueries Search => new(this);

    /// <summary>The download endpoints: CDN files streamed to disk.</summary>
    public ModrinthDownloadQueries Downloads => new(this);

    // ------------------------------------------------------------------ executor face

    /// <summary>Pure transport: the PAT header rides on the request when a token is set,
    /// and the response streams back with only its headers received.</summary>
    async Task<HttpResponseMessage> IApiExecutor.SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (Options.ApiToken.Length != 0)
        {
            request.Headers.TryAddWithoutValidation("Authorization", Options.ApiToken);
        }

        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
    }
}
