// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

namespace FlintLauncher.ExternalApi.Technic;

/// <summary>The client for the Technic Platform API and the Solder instances its packs
/// point at. The groups hand out the endpoint operations; send each with
/// <c>ApiOperation.SendTo</c> through the HttpClient of the caller's choice.</summary>
public sealed class TechnicClient(TechnicClientOptions? options = null)
{
    /// <summary>The Platform base URL and API build the operations read per send; apply
    /// changes through the same instance.</summary>
    public TechnicClientOptions Options { get; } = options ?? new TechnicClientOptions();

    // ------------------------------------------------------------------ endpoint groups

    /// <summary>The search endpoint: trending, search or a single-pack lookup, selected by
    /// the term.</summary>
    public TechnicSearchQueries Search => new(this);

    /// <summary>The per-slug pack metadata endpoint.</summary>
    public TechnicPackQueries Packs => new(this);

    /// <summary>The Solder endpoints of a pack's own Solder instance (independent of the
    /// Platform options).</summary>
    public TechnicSolderQueries Solder => new();
}
