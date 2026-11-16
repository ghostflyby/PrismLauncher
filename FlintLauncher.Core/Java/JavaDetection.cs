// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

using JdkFind;
using TruePath;

namespace FlintLauncher.Core.Java;

public static class JavaDetection
{
    /// <summary>Runs the executable-oriented detection pipeline: the JdkFind provider suite
    /// (or the injected providers — the test seam) plus the launcher-owned providers. Results
    /// keep first-discovery order; JdkFind deduplicates by canonical executable path.
    /// <paramref name="managedOnly"/> is the C++ "managed versions" probe: only the
    /// launcher-managed java root is scanned (no production caller passes it; kept for
    /// upstream parity).</summary>
    public static Task<IReadOnlyList<JavaExecutable>> DetectAsync(
        AbsolutePath? javaRoot = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<IJvmProvider>? providers = null,
        bool managedOnly = false) =>
        BuildFinder(javaRoot, providers, managedOnly).LocateExecutablesAsync(cancellationToken);

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
                Providers =
                [
                    .. providers,
                    new ManagedJavaRootJvmProvider(javaRoot),
                    new JavaPathsJvmProvider(),
                ],
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
                new JavaPathsJvmProvider(),
            ],
            ExecutableDirectories =
            [
                .. JdkFinder.Default.ExecutableDirectories,
                .. LauncherOwnedExecutableDirectories.ForManagedRoot(javaRoot),
                .. LauncherOwnedExecutableDirectories.ForMinecraftRuntime(),
            ],
        };
    }

}
