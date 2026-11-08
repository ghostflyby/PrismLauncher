// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

namespace FlintLauncher.ExternalApi.Paste;

/// <summary>The client for the configured paste provider (0x0.st, hastebin, paste.gg or
/// mclo.gs). The group hands out the upload operation; send it with
/// <c>ApiOperation.SendTo</c> through the HttpClient of the caller's choice.</summary>
public sealed class PasteClient(PasteClientOptions? options = null)
{
    /// <summary>PasteUpload::PasteTypes (same table, same order as <see cref="PasteType"/>).</summary>
    public static IReadOnlyList<PasteTypeInfo> Types { get; } =
    [
        new("0x0.st", "https://0x0.st", ""),
        new("hastebin", "https://hst.sh", "/documents"),
        new("paste.gg", "https://paste.gg", "/api/v1/pastes"),
        new("mclo.gs", "https://api.mclo.gs", "/1/log"),
    ];

    /// <summary>The provider and base the operations read per send; apply changes through
    /// the same instance.</summary>
    public PasteClientOptions Options { get; } = options ?? new PasteClientOptions();

    // ------------------------------------------------------------------ endpoint groups

    /// <summary>The paste endpoint: one text upload.</summary>
    public PasteQueries Pastes => new(this);
}
