// SPDX-License-Identifier: GPL-3.0-only
// Port of libraries/javacheck: upstream compiles JavaCheck.java into a small jar at build time
// and installs it next to the launcher (Application::getJarPath finds it at runtime). Flint
// phase P5-a does not build or embed the jar (no build-system wiring exists yet), so
// extraction always fails with the stub reason below; the parent wires the packaged jar in
// P5-c. Meanwhile a compiled jar can simply be dropped into <app>/jars/JavaCheck.jar —
// JavaUtils.GetJavaCheckPath() picks it up like the C++ original does.
namespace FlintLauncher.Core.Java;

public static class JavaCheckJar
{
    /// <summary>The jar file name the C++ build installs (getJarPath("JavaCheck.jar")).</summary>
    public const string JarFileName = "JavaCheck.jar";

    /// <summary>Where an embedded checker would be extracted (per-user temp/cache dir).</summary>
    public static string DefaultExtractionPath { get; } =
        IO.FileSystem.PathCombine(Path.GetTempPath(), "Flint", JarFileName);

    /// <summary>Why <see cref="TryExtractTo"/> fails in this phase; surfaced by JavaChecker results.</summary>
    public static string StubReason { get; } =
        "JavaCheck.jar is not embedded in this build (P5-a stub; the packaged jar is wired in P5-c). " +
        "Place a compiled JavaCheck.jar into <application dir>/jars/ to enable Java checks.";

    /// <summary>
    /// Port of the (not yet existing) embedded-jar extraction: writes the jar compiled from
    /// libraries/javacheck/JavaCheck.java to <paramref name="targetPath"/>. Always returns
    /// false in P5-a with <paramref name="error"/> set to <see cref="StubReason"/>.
    /// </summary>
    public static bool TryExtractTo(string targetPath) => TryExtractTo(targetPath, out _);

    public static bool TryExtractTo(string targetPath, out string error)
    {
        error = StubReason;
        return false;
    }
}
