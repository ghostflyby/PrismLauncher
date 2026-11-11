// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

namespace FlintLauncher.ExternalApi.Ftb;

/// <summary>The FTB client configuration: the modpacks.ch API base URL, read per request —
/// apply changes through the instance the client carries. The base carries no trailing
/// slash; the operations concatenate "/modpack/..." onto it.</summary>
public sealed class FtbClientOptions
{
    public string BaseUrl { get; set; } = "https://api.feed-the-beast.com/v1/modpacks/public";
}
