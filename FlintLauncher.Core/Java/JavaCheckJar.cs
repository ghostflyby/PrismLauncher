// SPDX-License-Identifier: GPL-3.0-only
// Port of libraries/javacheck: upstream compiles JavaCheck.java into a small jar at build time
// and installs it next to the launcher (Application::getJarPath finds it at runtime). Flint
// builds it with docs/tools/newlaunch-build.sh into the same jars/ layout; this helper finds
// that jar (the same launcher-root/jars lookup as Application::getJarPath) and copies it to
// the per-user extraction path JavaChecker expects.
namespace FlintLauncher.Core.Java;

public static class JavaCheckJar
{
    /// <summary>The jar file name the C++ build installs (getJarPath("JavaCheck.jar")).</summary>
    public const string JarFileName = "JavaCheck.jar";

    /// <summary>Where the checker jar is extracted for JavaChecker (per-user temp/cache dir).</summary>
    public static string DefaultExtractionPath { get; } =
        IO.FileSystem.PathCombine(Path.GetTempPath(), "Flint", JarFileName);

    /// <summary>Why <see cref="TryExtractTo"/> failed; surfaced by JavaChecker results.</summary>
    public static string StubReason { get; } =
        "JavaCheck.jar could not be found. Please check your installation " +
        "(build it with docs/tools/newlaunch-build.sh and place it into <launcher root>/jars/).";

    /// <summary>
    /// Port of Application::getJarPath(JarFileName) + install: find the compiled JavaCheck.jar
    /// in the launcher installation layout and copy it to <paramref name="targetPath"/>.
    /// Returns false (with <paramref name="error"/> set) when the jar is not installed.
    /// </summary>
    public static bool TryExtractTo(string targetPath) => TryExtractTo(targetPath, out _);

    public static bool TryExtractTo(string targetPath, out string error)
    {
        var jarPath = GetInstalledJarPath();
        if (jarPath.Length == 0)
        {
            error = StubReason;
            return false;
        }
        try
        {
            IO.FileSystem.EnsureFilePathExists(targetPath);
            File.Copy(jarPath, targetPath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error = $"Failed to extract {JarFileName} to {targetPath}: {e.Message}";
            return false;
        }
        error = "";
        return true;
    }

    /// <summary>Port of Application::getJarPath("JavaCheck.jar") (see LauncherJars.GetJarPath).</summary>
    public static string GetInstalledJarPath()
    {
        var potentialPaths = new List<string>();
        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD())
        {
            potentialPaths.Add(IO.FileSystem.PathCombine(JavaUtils.LauncherRootPath, "share",
                FlintConfig.Default.LauncherName));
        }
        potentialPaths.Add(IO.FileSystem.PathCombine(JavaUtils.LauncherRootPath, "jars"));
        potentialPaths.Add(IO.FileSystem.PathCombine(AppContext.BaseDirectory, "jars"));
        potentialPaths.Add(IO.FileSystem.PathCombine(AppContext.BaseDirectory, "..", "jars")); // from inside build dir, for debugging

        foreach (var p in potentialPaths)
        {
            var jarPath = IO.FileSystem.PathCombine(p, JarFileName);
            if (File.Exists(jarPath))
            {
                return jarPath;
            }
        }
        return "";
    }
}
