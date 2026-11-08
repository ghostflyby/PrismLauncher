// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

namespace FlintLauncher.ExternalApi.Ftb;

/// <summary>The client for the FTB App API (modpacks.ch). The groups hand out the endpoint
/// operations; send each with <c>ApiOperation.SendTo</c> through the HttpClient of the
/// caller's choice.</summary>
public sealed class FtbClient(FtbClientOptions? options = null)
{
    /// <summary>The base URL the operations read per send; apply changes through the same
    /// instance.</summary>
    public FtbClientOptions Options { get; } = options ?? new FtbClientOptions();

    // ------------------------------------------------------------------ endpoint groups

    /// <summary>The modpack endpoints: the whole-index listing, the per-pack description and
    /// the per-version payload.</summary>
    public FtbPackQueries Packs => new(this);
}
