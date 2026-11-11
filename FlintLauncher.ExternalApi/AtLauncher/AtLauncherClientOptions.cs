// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

namespace FlintLauncher.ExternalApi.AtLauncher;

/// <summary>The ATLauncher client configuration, read per request — apply changes through
/// the instance the client carries. Both bases carry a trailing slash; the operations and
/// URL builders concatenate their paths directly onto them.</summary>
public sealed class AtLauncherClientOptions
{
    /// <summary>The ATLauncher API base (the share-code lookups).</summary>
    public string ApiBaseUrl { get; set; } = "https://api.atlauncher.com/v1/";

    /// <summary>The download server: the packsnew.json index, the per-version Configs.json
    /// and Configs.zip, the pack images and the relative mod/library downloads.</summary>
    public string DownloadServerUrl { get; set; } = "https://download.nodecdn.net/containers/atl/";
}
