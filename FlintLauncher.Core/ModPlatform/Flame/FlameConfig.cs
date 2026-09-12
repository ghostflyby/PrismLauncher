// SPDX-License-Identifier: GPL-3.0-only
// Port of the Flame (CurseForge) build-config and Application-singleton hooks consumed by
// the Flame API layer: BuildConfig.FLAME_BASE_URL / FLAME_DOWNLOAD_HOST and the
// APPLICATION->getFlameAPIKey() + Application::SupportsFlame capability of the
// net/ApiHeaderProxy.
//
// API key handling: the CurseForge API key in the upstream CMakeLists is issued to
// Prism Launcher and must NOT be reused by derivative works (see the FlintConfig.FlameApiKey
// note), so Flint ships without one — the default provider returns the (empty)
// FlintConfig.FlameApiKey. Embedders wire their own key through ApiKeyProvider (e.g. from
// the "FlameKeyOverride" application setting); a non-empty key is sent as the "x-api-key"
// header on every Flame API request (the C++ ApiHeaderProxy attaches it for requests to the
// Flame API/download hosts).
namespace FlintLauncher.Core.ModPlatform.Flame;

public static class FlameConfig
{
    /// <summary>BuildConfig.FLAME_BASE_URL (settable; tests restore it — see FlameApiTests).</summary>
    public static string BaseUrl { get; set; } = FlintConfig.Default.FlameBaseUrl;

    /// <summary>BuildConfig.FLAME_DOWNLOAD_HOST (the mod file CDN).</summary>
    public static string DownloadHost { get; set; } = FlintConfig.Default.FlameDownloadHost;

    /// <summary>
    /// APPLICATION->getFlameAPIKey() hook. Flint ships no CurseForge key: the default reads
    /// FlintConfig.FlameApiKey, which is empty unless the user overrides it.
    /// </summary>
    public static Func<string> ApiKeyProvider { get; set; } = static () => FlintConfig.Default.FlameApiKey;
}
