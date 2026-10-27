// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

using FlintLauncher.ExternalApi;
using FlintLauncher.ExternalApi.Modrinth;

namespace FlintLauncher.Tests;

/// <summary>Test factory for hermetic Modrinth client pairs: every client gets a throwaway
/// HttpClient (nothing process-global to leak in or out) pointed
/// at the suite's stub — or at a discard address when the suite never exercises the Modrinth
/// paths.</summary>
internal static class TestModrinthApi
{
    public static (ModrinthClient Client, HttpClient Http) At(string baseUrl)
    {
        // UseProxy=false keeps the throwaway client hermetic: a system proxy would otherwise
        // intercept the loopback stub traffic (and answer 502)
        var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        var options = new ModrinthClientOptions { BaseUrl = baseUrl };
        return (new ModrinthClient(options), http);
    }

    /// <summary>An API wired to a closed port: any surprise request fails fast and locally
    /// instead of reaching the real service.</summary>
    public static (ModrinthClient Client, HttpClient Http) Unreachable() =>
        At("http://127.0.0.1:9/");
}
