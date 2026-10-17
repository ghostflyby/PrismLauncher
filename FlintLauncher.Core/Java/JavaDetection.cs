// SPDX-License-Identifier: GPL-3.0-only
// The JVM detection pipeline, fully on JdkFind facilities: LocateAsync is the primary
// source (validated installations whose release file is enriched by the runtime probe),
// and the C++-parity legacy/launcher scan survives only as a candidate producer for what
// discovery cannot see — each of those candidates is probed through FromExecutable
// (path-keyed, sanitized child environment, verdict on StartFailure). The JavaCheck.jar
// checker is gone from this path entirely.
//
// Deviations from the C++ original (launcher/java/JavaInstallList.cpp): the detection list
// never runs JavaCheck; primary entries stay listed with release-file metadata even when
// their probe failed (degrade-and-keep), and legacy candidates without any establishable
// JVM drop instead of listing as unknown.

using JdkFind;

namespace FlintLauncher.Core.Java;

/// <summary>One detected JVM candidate. Primary-layer entries (LocateAsync) are always
/// valid: a located home has a parseable release file, and a failed runtime probe merely
/// degrades the metadata. Fallback-layer entries carry the FromExecutable verdict — a
/// <see cref="JavaExecutable.StartFailure"/> lists the candidate as invalid ("unknown"
/// placeholders; the /java view keeps it, the install list filters it), and a candidate
/// with no establishable JVM drops entirely.</summary>
public sealed record JavaCandidate(string Path, string Version, string Architecture, bool Valid);

public static class JavaDetection
{
    /// <summary>Runs the detection pipeline: the JdkFind provider suite (or the injected
    /// providers — the test seam) locates validated installations, then the legacy/launcher
    /// candidates that were not already covered go through FromExecutable. Deduplication
    /// across the layers is exact-string on the executable path, matching the upstream list
    /// semantics. <paramref name="managedOnly"/> is the C++ "managed versions" probe: the
    /// primary layer is skipped and only the launcher-managed java root is probed (no
    /// production caller passes it; kept for upstream parity).</summary>
    public static async Task<IReadOnlyList<JavaCandidate>> DetectAsync(
        string? javaRoot = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<IJvmProvider>? providers = null,
        bool managedOnly = false)
    {
        var results = new List<JavaCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Primary: the provider suite with the runtime probe enabled (the default). The
        // probed binary itself is the candidate — the Executable carries the resolved path
        // (JDK 8 inner-JRE layouts included), the probed version and the reported
        // architecture, each falling back to the release file.
        if (!managedOnly)
        {
            var jvms = providers is null
                ? await JdkFinder.Default.LocateAsync(cancellationToken).ConfigureAwait(false)
                : await new JdkFinder { Providers = providers }.LocateAsync(cancellationToken).ConfigureAwait(false);
            foreach (var jvm in jvms)
            {
                var executable = jvm.Executable;
                if (!seen.Add(executable.Path))
                {
                    continue;
                }
                results.Add(new JavaCandidate(executable.Path, executable.Version.Original,
                    Architecture(executable), Valid: true));
            }
        }

        // Fallback: the legacy/launcher candidate paths (release-less JVMs, the extra distro
        // directories, the Minecraft runtime bundle, the managed java root, the env
        // override), probed path-by-path. A candidate that runs is valid; one backed by a
        // release file but unable to start lists as invalid; one with no establishable JVM
        // drops.
        var fallback = (managedOnly
                ? JavaUtils.GetPrismJavaBundle(javaRoot)
                : JavaUtils.FindJavaPaths(javaRoot))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Where(path => !seen.Contains(path))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (fallback.Count > 0)
        {
            var probed = new JavaExecutable?[fallback.Count];
            await Parallel.ForEachAsync(
                Enumerable.Range(0, fallback.Count),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Environment.ProcessorCount,
                    CancellationToken = cancellationToken,
                },
                async (index, token) =>
                    probed[index] = await JdkFinder.Default.FromExecutableAsync(fallback[index], token)
                        .ConfigureAwait(false))
                .ConfigureAwait(false);

            foreach (var executable in probed.OfType<JavaExecutable>())
            {
                if (!seen.Add(executable.Path))
                {
                    continue;
                }
                if (executable.StartFailure is not null)
                {
                    results.Add(new JavaCandidate(executable.Path, "unknown", "unknown", Valid: false));
                    continue;
                }
                results.Add(new JavaCandidate(executable.Path, executable.Version.Original,
                    Architecture(executable), Valid: true));
            }
        }

        return results;
    }

    /// <summary>The reported os.arch — the platform specification guarantees the property,
    /// so only an unprobed binary can leave it empty.</summary>
    private static string Architecture(JavaExecutable executable) =>
        executable.Architecture.Length == 0 ? "unknown" : executable.Architecture;
}
