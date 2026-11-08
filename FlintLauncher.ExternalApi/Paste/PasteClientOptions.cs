// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

namespace FlintLauncher.ExternalApi.Paste;

/// <summary>The paste providers (PasteUpload::PasteType); the order is the stored settings
/// index.</summary>
public enum PasteType
{
    // 0x0.st
    NullPointer,

    // hastebin.com
    Hastebin,

    // paste.gg
    PasteGg,

    // mclo.gs
    Mclogs,
}

/// <summary>One provider's table entry: the display name, the default base and the endpoint
/// path appended to the base.</summary>
public sealed record PasteTypeInfo(string Name, string DefaultBase, string EndpointPath);

/// <summary>The paste client configuration: the provider and its base, read per request —
/// apply changes through the instance the client carries.</summary>
public sealed class PasteClientOptions
{
    public PasteType Type { get; set; } = PasteType.Mclogs;

    /// <summary>The provider base; "" selects the provider's default base.</summary>
    public string BaseUrl { get; set; } = "";
}
