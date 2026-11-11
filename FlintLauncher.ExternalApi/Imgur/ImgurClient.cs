// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

namespace FlintLauncher.ExternalApi.Imgur;

/// <summary>The client for the imgur API (anonymous image uploads and albums). The groups
/// hand out the endpoint operations; send each with <c>ApiOperation.SendTo</c> through the
/// HttpClient of the caller's choice.</summary>
public sealed class ImgurClient(ImgurClientOptions? options = null)
{
    /// <summary>The base URL and client id the operations read per send; apply changes
    /// through the same instance.</summary>
    public ImgurClientOptions Options { get; } = options ?? new ImgurClientOptions();

    // ------------------------------------------------------------------ endpoint groups

    /// <summary>The image endpoint: one image upload.</summary>
    public ImgurImageQueries Images => new(this);

    /// <summary>The album endpoint: grouping uploaded images by their delete hashes.</summary>
    public ImgurAlbumQueries Albums => new(this);
}
