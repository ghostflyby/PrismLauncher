// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// The JVM detection pipeline, executable-first: JdkFinder.LocateExecutablesAsync returns
// every located installation's own java plus the standalone candidates from the configured
// executable directories. The launcher-owned sources JdkFind cannot know about are added as
// providers (the managed java root and the official launcher's runtime bundles);
// FLINT_JAVA_PATHS entries are probed explicitly because they name executables, not homes.
// The JavaCheck.jar checker is gone from this path entirely.
//
// Deviations from the C++ original (launcher/java/JavaInstallList.cpp): a failed standalone
// probe remains listed with StartFailure instead of being rewritten to "unknown"
// placeholders, and a candidate without any establishable JVM drops.

using JdkFind;
using TruePath;

namespace FlintLauncher.Core.Java;

public static class JavaDetection
{
    /// <summary>Runs the executable-oriented detection pipeline: the JdkFind provider suite
    /// (or the injected providers — the test seam) plus the launcher-owned providers, then
    /// the explicit FLINT_JAVA_PATHS entries. Results keep first-discovery order and are
    /// deduplicated by executable path. <paramref name="managedOnly"/> is the C++ "managed
    /// versions" probe: only the launcher-managed java root is scanned (no production caller
    /// passes it; kept for upstream parity).</summary>
    public static async Task<IReadOnlyList<JavaExecutable>> DetectAsync(
        AbsolutePath? javaRoot = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<IJvmProvider>? providers = null,
        bool managedOnly = false)
    {
        var located = await BuildFinder(javaRoot, providers, managedOnly)
            .LocateExecutablesAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<JavaExecutable>(located);
        if (!managedOnly)
        {
            var seen = new HashSet<string>(PathComparer);
            foreach (var executable in located)
            {
                seen.Add(CanonicalExecutablePath(executable.Path));
            }

            results.AddRange(await ProbeEnvironmentPathsAsync(seen, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    private static JdkFinder BuildFinder(AbsolutePath? javaRoot, IReadOnlyList<IJvmProvider>? providers,
        bool managedOnly)
    {
        if (managedOnly)
        {
            return new JdkFinder
            {
                Providers = [new ManagedJavaRootJvmProvider(javaRoot)],
                ExecutableDirectories = [.. LauncherOwnedExecutableDirectories.ForManagedRoot(javaRoot)],
            };
        }

        // An injected provider list is the test seam: it replaces the system suite and the
        // default PATH scan so synthetic homes never compete with the machine's JVMs.
        if (providers is not null)
        {
            return JdkFinder.Default with
            {
                Providers = [.. providers, new ManagedJavaRootJvmProvider(javaRoot)],
                ExecutableDirectories = [],
            };
        }

        return JdkFinder.Default with
        {
            Providers =
            [
                .. JdkFinder.Default.Providers,
                new ManagedJavaRootJvmProvider(javaRoot),
                new MinecraftRuntimeJvmProvider(),
            ],
            ExecutableDirectories =
            [
                .. JdkFinder.Default.ExecutableDirectories,
                .. LauncherOwnedExecutableDirectories.ForManagedRoot(javaRoot),
                .. LauncherOwnedExecutableDirectories.ForMinecraftRuntime(),
            ],
        };
    }

    /// <summary>Probes the explicit executable paths in FLINT_JAVA_PATHS (upstream's
    /// addJavasFromEnv): a file is probed verbatim, a directory contributes its java
    /// executable. These are executable references, not homes, so they cannot ride the
    /// provider suite.</summary>
    private static async Task<IReadOnlyList<JavaExecutable>> ProbeEnvironmentPathsAsync(
        HashSet<string> seen, CancellationToken cancellationToken)
    {
        var value = Environment.GetEnvironmentVariable($"{FlintConfig.Default.LauncherAppBinaryEnvName}_JAVA_PATHS") ?? "";
        var paths = value.Split(OperatingSystem.IsWindows() ? ';' : ':',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Resolve and deduplicate before probing: an env entry that repeats an already
        // located executable must not launch another probe.
        var environmentSeen = new HashSet<string>(PathComparer);
        var candidates = new List<string>(paths.Length);
        foreach (var path in paths)
        {
            var executable = Directory.Exists(path) ? Path.Combine(path, JavaUtils.JavaExecutable) : path;
            if (!File.Exists(executable))
            {
                continue;
            }

            var canonical = CanonicalExecutablePath(executable);
            if (seen.Contains(canonical) || !environmentSeen.Add(canonical))
            {
                continue;
            }

            candidates.Add(executable);
        }

        if (candidates.Count == 0)
        {
            return [];
        }

        var probed = new JavaExecutable?[candidates.Count];
        await Parallel.ForEachAsync(
                Enumerable.Range(0, candidates.Count),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Environment.ProcessorCount,
                    CancellationToken = cancellationToken,
                },
                async (index, token) => probed[index] =
                    await JdkFinder.Default.FromExecutableAsync(candidates[index], token).ConfigureAwait(false))
            .ConfigureAwait(false);

        return probed.OfType<JavaExecutable>().ToList();
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    /// <summary>Resolves the executable's symlink chain, falling back to the full path when
    /// the target cannot be read.</summary>
    private static string CanonicalExecutablePath(string path)
    {
        try
        {
            return Path.GetFullPath(File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Path.GetFullPath(path);
        }
    }
}
