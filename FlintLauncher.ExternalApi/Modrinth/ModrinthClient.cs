// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

namespace FlintLauncher.ExternalApi.Modrinth;

/// <summary>The client for the Modrinth v2 API. The groups hand out the endpoint
/// operations; send each with <c>ApiOperation.SendTo</c> through the HttpClient of the
/// caller's choice.</summary>
public sealed class ModrinthClient(ModrinthClientOptions? options = null)
{
    /// <summary>The base URL and PAT the operations read per send; apply changes through
    /// the same instance.</summary>
    public ModrinthClientOptions Options { get; } = options ?? new ModrinthClientOptions();

    /// <summary>The CDN host the mrpack import validates download links against.</summary>
    public string DownloadHost { get; set; } = "cdn.modrinth.com";

    public static IReadOnlyList<string> MrpackHosts { get; } =
        ["cdn.modrinth.com", "github.com", "raw.githubusercontent.com", "gitlab.com"];

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
}
