// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

namespace FlintLauncher.ExternalApi.Technic;

/// <summary>The Technic Platform client configuration, read per request — apply changes
/// through the instance the client carries.</summary>
public sealed class TechnicClientOptions
{
    /// <summary>The Platform API base; carries a trailing slash (the operations concatenate
    /// their paths directly onto it).</summary>
    public string BaseUrl { get; set; } = "https://api.technicpack.net/";

    /// <summary>The ?build= value every Platform request carries.</summary>
    public string ApiBuild { get; set; } = "multimc";
}
