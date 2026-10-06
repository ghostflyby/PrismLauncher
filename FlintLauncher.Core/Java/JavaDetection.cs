// SPDX-License-Identifier: GPL-3.0-only
// The JVM detection pipeline's shared core: JdkFind's LocateAsync is the primary source —
// validated installations whose metadata comes from the release file, enriched by the probe
// executing each candidate's own java (RuntimeVersion / os.arch fallback). The C++-parity
// legacy/launcher scan survives as the fallback for candidates without a release file (old
// Apple/Oracle runtimes); those still go through a JavaChecker so they keep metadata and a
// validity verdict.
//
// Deviation from the C++ original (launcher/java/JavaInstallList.cpp): the detection list no
// longer runs JavaCheck.jar for every candidate — the probe executes each JVM once and
// degrades to release-file metadata on failure, and the launch-time ground truth stays with
// CheckJava (signature-gated; Errored rejects the launch). MojangPlatform never had a
// consumer on the detection path (the list stores the real os.arch), so nothing is lost.
using JdkFind;
using FlintLauncher.Core.Tasks;

namespace FlintLauncher.Core.Java;

/// <summary>One detected JVM candidate. Primary-layer entries (JdkFind) are always valid: a
/// located home has a parseable release file, and a failed runtime probe merely degrades the
/// metadata. Fallback-layer entries carry the JavaChecker verdict; an invalid candidate keeps
/// "unknown" placeholders (the /java view lists them, the install list filters them).</summary>
public sealed record JavaCandidate(string Path, string Version, string Architecture, bool Valid);

public static class JavaDetection
{
    /// <summary>Runs the detection pipeline: the JdkFind provider suite (or the injected
    /// providers — the test seam) locates validated installations, then the legacy/launcher
    /// candidates that were not already covered go through JavaChecker. Deduplication across
    /// the layers is exact-string on the executable path, matching the upstream list
    /// semantics. <paramref name="managedOnly"/> is the C++ "managed versions" probe: the
    /// primary layer is skipped and only the launcher-managed java root is checker-validated
    /// (no production caller passes it; kept for upstream parity).</summary>
    public static async Task<IReadOnlyList<JavaCandidate>> DetectAsync(
        string? javaRoot = null,
        int maxConcurrent = ConcurrentTask.DefaultMaxConcurrent,
        CancellationToken cancellationToken = default,
        IReadOnlyList<IJvmProvider>? providers = null,
        Action<TaskStepProgress>? progress = null,
        bool managedOnly = false)
    {
        var results = new List<JavaCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Primary: the provider suite with the runtime probe enabled (the default) — a listed
        // java has executed once, which is the guarantee the per-candidate JavaChecker run
        // used to give the list, without JavaCheck.jar.
        if (!managedOnly)
        {
            var jvms = providers is null
                ? await JdkFinder.Default.LocateAsync(cancellationToken).ConfigureAwait(false)
                : await new JdkFinder { Providers = providers }.LocateAsync(cancellationToken).ConfigureAwait(false);
            foreach (var jvm in jvms)
            {
                if (ExecutablePath(jvm) is not { } path || !seen.Add(path))
                {
                    continue;
                }
                results.Add(new JavaCandidate(path, jvm.RuntimeVersion ?? jvm.Version.Original,
                    jvm.Architecture ?? "unknown", Valid: true));
            }
        }

        // Fallback: the legacy/launcher candidate paths (release-less JVMs, the extra distro
        // directories, the Minecraft runtime bundle, the managed java root, the env override).
        // JavaChecker remains the validity gate and the metadata source on this layer.
        var fallback = (managedOnly ? JavaUtils.GetPrismJavaBundle(javaRoot) : JavaUtils.FindJavaPaths(javaRoot))
            .Where(path => !seen.Contains(path))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (fallback.Count > 0)
        {
            var job = new ConcurrentTask("Java detection fallback", maxConcurrent);
            if (progress is not null)
            {
                job.Progress += (_, stepProgress) => progress(stepProgress);
            }

            var checkResults = new List<JavaChecker.Result>();
            var id = 0;
            foreach (var path in fallback)
            {
                var checker = new JavaChecker(path, "", id: id);
                checker.CheckFinished += result => checkResults.Add(result);
                job.AddTask(checker);
                id++;
            }

            await job.StartAsync(cancellationToken).ConfigureAwait(false);

            // the C++ connects Task::finished (fires on success and failure alike)
            foreach (var result in checkResults.OrderBy(r => r.Id))
            {
                var valid = result.Validity == JavaChecker.Validity.Valid;
                results.Add(new JavaCandidate(
                    result.Path,
                    valid ? result.JavaVersion.ToString() : "unknown",
                    valid && result.RealPlatform.Length > 0 ? result.RealPlatform : "unknown",
                    valid));
            }
        }

        return results;
    }

    /// <summary>The launcher executable of a located installation. JdkFind's probe contract
    /// guarantees bin/java (or the JDK 8 inner-JRE jre/bin/java) exists, so Resolve is
    /// expected to hit; the plain "java" fallback covers a Windows home that ships java.exe
    /// but no javaw.exe, where the launcher-preferred javaw.exe would otherwise dead-end.</summary>
    private static string? ExecutablePath(Jvm jvm) =>
        jvm.Resolve(JavaUtils.JavaExecutable)?.FullName ?? jvm.Resolve("java")?.FullName;
}
