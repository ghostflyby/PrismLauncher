// SPDX-License-Identifier: GPL-3.0-only
// Port of Application::getJarPath (launcher/Application.cpp) plus the two executable-lookup
// helpers the launch steps need (FS::ResolveExecutable and QStandardPaths::findExecutable —
// both were explicitly not ported into IO/FileSystem).
//
// The jar layout matches the C++ original: jars live in <launcher root>/jars (with the
// Linux "share/<launcher name>" variant) next to the application directory — the jars are
// build outputs produced by docs/tools/newlaunch-build.sh and are NOT committed.
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Java;

namespace FlintLauncher.Core.Launch;

public static class LauncherJars
{
    /// <summary>NewLaunch.jar: the java launcher part the game process runs.</summary>
    public const string NewLaunchJarName = "NewLaunch.jar";

    /// <summary>NewLaunchLegacy.jar: the legacy/applet launcher variant.</summary>
    public const string NewLaunchLegacyJarName = "NewLaunchLegacy.jar";

    /// <summary>Port of Application::getJarPath(jarFile): find an installed launcher jar.</summary>
    public static string GetJarPath(string jarFile)
    {
        var potentialPaths = new List<string>();
        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD())
        {
            potentialPaths.Add(FileSystem.PathCombine(JavaUtils.LauncherRootPath, "share",
                FlintConfig.Default.LauncherName));
        }
        potentialPaths.Add(FileSystem.PathCombine(JavaUtils.LauncherRootPath, "jars"));
        potentialPaths.Add(FileSystem.PathCombine(AppContext.BaseDirectory, "jars"));
        potentialPaths.Add(FileSystem.PathCombine(AppContext.BaseDirectory, "..", "jars")); // from inside build dir, for debugging

        foreach (var p in potentialPaths)
        {
            var jarPath = FileSystem.PathCombine(p, jarFile);
            if (File.Exists(jarPath))
            {
                return jarPath;
            }
        }

        // nothing installed: extract the embedded copy to the per-user cache
        if (jarFile is NewLaunchJarName or NewLaunchLegacyJarName or Java.JavaCheckJar.JarFileName)
        {
            var extracted = Java.EmbeddedJars.EnsureExtracted(jarFile);
            return File.Exists(extracted) ? extracted : "";
        }
        return "";
    }

    /// <summary>Port of QStandardPaths::findExecutable: locate `name` on PATH; "" when not found.</summary>
    public static string FindExecutable(string name)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
        {
            return "";
        }
        var suffix = OperatingSystem.IsWindows() ? ".exe" : "";
        var hasExtension = name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && suffix.Length != 0;
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, name);
            if (!hasExtension)
            {
                candidate += suffix;
            }
            if (IsExecutableFile(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }
        return "";
    }

    /// <summary>Port of FS::ResolveExecutable: resolve a (possibly bare) executable path;
    /// "" when it cannot be resolved or is not executable.</summary>
    public static string ResolveExecutable(string path)
    {
        if (path.Length == 0)
        {
            return "";
        }
        if (!path.Contains('/'))
        {
            path = FindExecutable(path);
        }
        if (path.Length == 0)
        {
            return "";
        }
        var fullPath = Path.GetFullPath(path);
        return IsExecutableFile(fullPath) ? fullPath : "";
    }

    private static bool IsExecutableFile(string path)
    {
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists)
        {
            return false;
        }
        if (OperatingSystem.IsWindows())
        {
            return true; // QFileInfo::isExecutable on Windows checks the extension; File.Exists is enough here
        }
        try
        {
            var mode = fileInfo.UnixFileMode;
            return (mode & (System.IO.UnixFileMode.UserExecute | System.IO.UnixFileMode.GroupExecute |
                            System.IO.UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
