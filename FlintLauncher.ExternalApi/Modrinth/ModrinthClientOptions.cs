// SPDX-License-Identifier: GPL-3.0-only

namespace FlintLauncher.ExternalApi;

/// <summary>The Modrinth client's configuration, read per request from the instance the
/// composition root handed to the client. Apply settings changes through that same instance
/// (the Core adapter forwards its properties here); transport concerns themselves — proxy,
/// user agent, timeouts, retries — stay on the injected HttpClient.</summary>
public sealed class ModrinthClientOptions
{
    public string BaseUrl { get; set; } = "https://api.modrinth.com/v2";

    /// <summary>Optional user PAT; "" disables the Authorization header.</summary>
    public string ApiToken { get; set; } = "";
}
