// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

namespace FlintLauncher.ExternalApi.Imgur;

/// <summary>The imgur client configuration, read per request — apply changes through the
/// instance the client carries.</summary>
public sealed class ImgurClientOptions
{
    /// <summary>The imgur API base; carries a trailing slash (the operations append
    /// "image" / "album").</summary>
    public string BaseUrl { get; set; } = "https://api.imgur.com/3/";

    /// <summary>The application's imgur client id, sent as "Client-ID &lt;id&gt;" (the host
    /// applies the product's id from its own configuration).</summary>
    public string ClientId { get; set; } = "";
}
