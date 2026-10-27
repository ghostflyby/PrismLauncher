// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

namespace FlintLauncher.ExternalApi;

/// <summary>The stateless navigation root of the typed Modrinth v2 endpoint operations: the
/// groups return operation values bound to the root's live Options (hosts and PAT). It
/// holds no executor — sending is the caller's SendTo with the HttpClient (or any
/// HttpMessageInvoker) of its choice; the operations own the read and decide the failure
/// shape, uniform across this root.</summary>
public sealed class ModrinthClient
{
    public ModrinthClient(ModrinthClientOptions? options = null)
    {
        Options = options ?? new();
    }

    /// <summary>The live configuration (hosts, PAT) the operations read per send; shared
    /// with whoever constructed it (the Core adapter forwards its properties here).</summary>
    public ModrinthClientOptions Options { get; }

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
