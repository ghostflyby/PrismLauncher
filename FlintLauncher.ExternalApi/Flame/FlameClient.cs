// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

namespace FlintLauncher.ExternalApi.Flame;

/// <summary>The client for the CurseForge v1 API. The groups hand out the endpoint
/// operations; send each with <c>ApiOperation.SendTo</c> through the HttpClient of the
/// caller's choice.</summary>
public sealed class FlameClient(FlameClientOptions? options = null)
{
    /// <summary>The base URL and API key the operations read per send; apply changes
    /// through the same instance.</summary>
    public FlameClientOptions Options { get; } = options ?? new FlameClientOptions();

    // ------------------------------------------------------------------ endpoint groups

    /// <summary>The project endpoints: single and batch fetches plus the description.</summary>
    public FlameProjectQueries Projects => new(this);

    /// <summary>The file endpoints: single and batch fetches, the per-project file list
    /// and the changelog.</summary>
    public FlameFileQueries Files => new(this);

    /// <summary>The fingerprint endpoints: the murmur2 batch match.</summary>
    public FlameFingerprintQueries Fingerprints => new(this);

    /// <summary>The category endpoints.</summary>
    public FlameCategoryQueries Categories => new(this);

    /// <summary>The search endpoint.</summary>
    public FlameSearchQueries Search => new(this);
}
