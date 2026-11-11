// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

namespace FlintLauncher.ExternalApi.AtLauncher;

/// <summary>The client for the ATLauncher API and download server. The groups hand out the
/// endpoint operations; send each with <c>ApiOperation.SendTo</c> through the HttpClient of
/// the caller's choice. The URL builders name the download-server files a caller fetches as
/// plain downloads.</summary>
public sealed class AtLauncherClient(AtLauncherClientOptions? options = null)
{
    /// <summary>The API and download-server bases the operations read per send; apply
    /// changes through the same instance.</summary>
    public AtLauncherClientOptions Options { get; } = options ?? new AtLauncherClientOptions();

    // ------------------------------------------------------------------ endpoint groups

    /// <summary>The pack endpoints: the packsnew.json index and the per-version
    /// Configs.json manifest.</summary>
    public AtLauncherPackQueries Packs => new(this);

    /// <summary>The share-code endpoint (the optional-mod selection of a website
    /// share code).</summary>
    public AtLauncherShareCodeQueries ShareCodes => new(this);

    // ------------------------------------------------------------ download-server files

    /// <summary>The per-version Configs.zip archive (PackInstallTask::installConfigs).</summary>
    public string PackConfigsZipUrl(string packSafeName, string versionName) =>
        $"{Options.DownloadServerUrl}packs/{packSafeName}/versions/{versionName}/Configs.zip";

    /// <summary>The pack image (the AtlListModel/AtlPage logo requests).</summary>
    public string PackImageUrl(string safeName) => $"{Options.DownloadServerUrl}launcher/images/{safeName}";
}
