// SPDX-License-Identifier: GPL-3.0-only
// On-demand extraction of the embedded launcher jars (NewLaunch/NewLaunchLegacy, built by
// the BuildJavaJars MSBuild target (javac/jar via Exec) and embedded into this assembly).
// Extracted to the per-user temp cache so LauncherJars finds them without any external
// installation layout.

using FlintLauncher.Core.IO;
using TruePath;

namespace FlintLauncher.Core.Java;

public static class EmbeddedJars
{
    /// <summary>Extraction target: &lt;temp&gt;/Flint/jars/&lt;name&gt;.jar.</summary>
    public static string CacheDir { get; } = Path.Combine(Path.GetTempPath(), "Flint", "jars");

    /// <summary>The assembly namespace prefix embedded resource names live under.</summary>
    private static string ResourcePrefix =>
        typeof(EmbeddedJars).Assembly.GetName().Name + ".jars.";

    /// <summary>
    /// Ensures the named jar (e.g. "JavaCheck.jar") is available on disk: installed launcher
    /// roots win, then the per-user cache, then the assembly's embedded copy. Returns the path
    /// or "" when the jar is neither installed nor embedded. <paramref name="installRoot"/> is
    /// the launcher installation root (FlintPaths.InstallRoot); the default keeps the .NET app
    /// base directory for bare fixtures.
    /// </summary>
    public static string EnsureExtracted(string jarFileName, string? installRoot = null)
    {
        // 1) explicit launcher root / app-local layouts first (host overrides win)
        var installed = LauncherJarsPath(jarFileName, installRoot);
        if (installed.Length != 0)
        {
            return installed;
        }

        // 2) already extracted to the cache and still present
        var cached = Path.Combine(CacheDir, jarFileName);
        if (File.Exists(cached))
        {
            return cached;
        }

        // 3) extract from the embedded resources
        var resourceName = ResourcePrefix + jarFileName;
        var assembly = typeof(EmbeddedJars).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return "";
        }

        FileOps.EnsureFilePathExists(new AbsolutePath(cached));
        using var target = File.Create(cached);
        stream.CopyTo(target);

        return cached;
    }

    private static string LauncherJarsPath(string jarFileName, string? installRoot)
    {
        var root = installRoot ?? AppContext.BaseDirectory;
        var candidates = new List<string>();
        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD())
        {
            candidates.Add(Path.Combine(root, "share", FlintConfig.Default.LauncherName));
        }

        candidates.Add(Path.Combine(root, "jars"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "jars"));
        foreach (var dir in candidates)
        {
            var path = Path.Combine(dir, jarFileName);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return "";
    }
}
