// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

namespace FlintLauncher.ExternalApi.Flame;

/// <summary>The CurseForge client configuration: the API base URL and the optional API key.
/// Both are read per request — apply changes through the instance the client carries.</summary>
public sealed class FlameClientOptions
{
    public string BaseUrl { get; set; } = "https://api.curseforge.com/v1";

    /// <summary>The CurseForge API key; "" sends no x-api-key header (Flint ships none —
    /// the host applies the user override from its own configuration).</summary>
    public string ApiKey { get; set; } = "";
}
