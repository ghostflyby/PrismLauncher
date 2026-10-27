// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

namespace FlintLauncher.ExternalApi.Modrinth;

/// <summary>The Modrinth client configuration: the API base URL and the optional PAT. Both
/// are read per request — apply changes through the instance the client carries.</summary>
public sealed class ModrinthClientOptions
{
    public string BaseUrl { get; set; } = "https://api.modrinth.com/v2";

    /// <summary>Optional user PAT; "" disables the Authorization header.</summary>
    public string ApiToken { get; set; } = "";
}
