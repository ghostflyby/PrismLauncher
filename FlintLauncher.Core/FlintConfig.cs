// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Immutable;

namespace FlintLauncher.Core;

/// <summary>
/// Build-time configuration of the launcher.
/// Port of buildconfig/BuildConfig.{h,cpp.in} with the default values from the repository CMakeLists
/// (root CMakeLists.txt lines 180-284 and program_info/CMakeLists.txt).
/// </summary>
public sealed class FlintConfig
{
    public static readonly FlintConfig Default = new();

    // ---- Identity -------------------------------------------------------------
    // Flint is a derivative work of PrismLauncher; it identifies itself as Flint but
    // keeps data-format compatibility (see docs/RESEARCH.md §0).

    public string LauncherName { get; init; } = "Flint";
    public string LauncherAppBinaryName { get; init; } = "flint";
    public string LauncherDisplayName { get; init; } = "Flint Launcher";
    public string LauncherCopyright { get; init; } = "© 2026 Flint Contributors";

    /// <summary>Domain of the upstream services Flint consumes (meta server, translations).</summary>
    public string LauncherDomain { get; init; } = "prismlauncher.org";

    /// <summary>
    /// Global config file name. Prism uses "prismlauncher.cfg"; Flint's own historical name
    /// was "flint.cfg" — since CONFIG C5 the global config is flint.json and this name is
    /// the IMPORT-ONLY chain anchor (FlintConfigImporter probe order) plus the legacy
    /// migration filter term; Flint never writes it.
    /// </summary>
    public string LauncherConfigFile { get; init; } = "flint.cfg";

    public string LauncherGit { get; init; } = "https://github.com/PrismLauncher/PrismLauncher";
    public string LauncherAppBinaryEnvName { get; init; } = "FLINT";

    public string UserAgent { get; init; } = $"Flint/{DefaultVersionString}";

    // ---- Version --------------------------------------------------------------
    // Flint's own version line. Prism's is 12.0.0; we restart at 0.1.0.

    public const string DefaultVersionString = "0.1.0";
    public int VersionMajor { get; init; } = 0;
    public int VersionMinor { get; init; } = 1;
    public int VersionPatch { get; init; } = 0;

    /// <summary>Update channel; TODO(P0+): derive from git branch / SourceRevisionId at build time.</summary>
    public string VersionChannel { get; init; } = "develop";

    public string GitCommit { get; init; } = "";
    public string GitTag { get; init; } = "";
    public string GitRefspec { get; init; } = "";

    public bool UpdaterEnabled { get; init; } = false; // GUI updaters are not ported
    public bool JavaDownloaderEnabled { get; init; } = true;

    public string BuildPlatform { get; init; } = "unknown";
    public string BuildArtifact { get; init; } = "";
    public string BuildDate { get; init; } = "";

    // ---- URLs (kept identical to Prism so data and metadata stay compatible) ---

    public string UpdaterGithubRepo { get; init; } = "https://github.com/PrismLauncher/PrismLauncher";

    // (the news feed URLs left with CACHE E7 — the checker they fed is deleted)
    public string WikiUrl { get; init; } = "https://prismlauncher.org/wiki/";
    public string HelpUrl { get; init; } = "https://prismlauncher.org/wiki/help-pages/%1";
    public string LoginCallbackUrl { get; init; } = "https://prismlauncher.org/successful-login";
    public string BugTrackerUrl { get; init; } = "https://github.com/PrismLauncher/PrismLauncher/issues";
    public string TranslationsUrl { get; init; } = "https://hosted.weblate.org/projects/prismlauncher/launcher/";
    public string TranslationFilesUrl { get; init; } = "https://i18n.prismlauncher.org/";
    public string MatrixUrl { get; init; } = "https://prismlauncher.org/matrix";
    public string DiscordUrl { get; init; } = "https://prismlauncher.org/discord";
    public string SubredditUrl { get; init; } = "https://prismlauncher.org/reddit";

    public string MetaUrl { get; init; } = "https://meta.prismlauncher.org/v1/";
    public string DefaultResourceBase { get; init; } = "https://resources.download.minecraft.net/";
    public string LibraryBase { get; init; } = "https://libraries.minecraft.net/";
    public string LegacyFmllibsBaseUrl { get; init; } = "https://files.prismlauncher.org/fmllibs/";

    // ---- API credentials --------------------------------------------------------

    public string ImgurClientId { get; init; } = "5b97b0713fba4a3";
    public string ImgurBaseUrl { get; init; } = "https://api.imgur.com/3/";

    // The MSA client id is shared by Prism builds per MS identity platform terms of use.
    public string MsaClientId { get; init; } = "c36a9fb6-4f2a-41ff-90bd-ae7cc92031eb";

    // IMPORTANT: the CurseForge API key in the upstream CMakeLists is issued specifically to
    // Prism Launcher and must NOT be reused by derivative works (see upstream CMakeLists.txt
    // comment). Flint ships without one; users set their own key via the FlameKeyOverride setting.
    public string FlameApiKey { get; init; } = "";

    // ---- Mod platforms ----------------------------------------------------------

    public string ModrinthStagingUrl { get; init; } = "https://staging-api.modrinth.com/v2";
    public string ModrinthProdUrl { get; init; } = "https://api.modrinth.com/v2";

    public ImmutableArray<string> ModrinthMrpackHosts { get; init; } =
    [
        "cdn.modrinth.com",
        "github.com",
        "raw.githubusercontent.com",
        "gitlab.com",
    ];

    public string ModrinthDownloadHost { get; init; } = "cdn.modrinth.com";

    public string FlameBaseUrl { get; init; } = "https://api.curseforge.com/v1";
    public string FlameDownloadHost { get; init; } = "edge.forgecdn.net";

    public string FtbApiBaseUrl { get; init; } = "https://api.feed-the-beast.com/v1/modpacks/public";
    public string LegacyFtbCdnBaseUrl { get; init; } = "https://dist.creeper.host/FTB2/";

    public string AtlDownloadServerUrl { get; init; } = "https://download.nodecdn.net/containers/atl/";
    public string AtlApiBaseUrl { get; init; } = "https://api.atlauncher.com/v1/";

    public string TechnicApiBaseUrl { get; init; } = "https://api.technicpack.net/";
    public string TechnicApiBuild { get; init; } = "multimc";

    // ---- Native library override names (per-OS, from root CMakeLists.txt) -------

    public string GlfwLibraryName { get; init; } = DefaultNativeLibraryName("libglfw.dylib", "libglfw.so", "glfw.dll");
    public string OpenalLibraryName { get; init; } = DefaultNativeLibraryName("libopenal.dylib", "libopenal.so", "OpenAL.dll");
    public string SdlLibraryName { get; init; } = DefaultNativeLibraryName("libSDL3.dylib", "libSDL3.so", "SDL3.dll");

    private static string DefaultNativeLibraryName(string macos, string linux, string windows) =>
        OperatingSystem.IsMacOS() ? macos : OperatingSystem.IsWindows() ? windows : linux;

    public string VersionString() => $"{VersionMajor}.{VersionMinor}.{VersionPatch}";

    /// <summary>Port of Config::printableVersionString(): appends the channel for non-stable builds.</summary>
    public string PrintableVersionString()
    {
        var version = VersionString();
        if (VersionChannel != "stable" && GitTag != version)
        {
            version += "-" + VersionChannel;
        }

        return version;
    }
}
