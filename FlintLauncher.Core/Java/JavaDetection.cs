// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

using JdkFind;
using TruePath;

namespace FlintLauncher.Core.Java;

public static class JavaDetection
{
    /// <summary>Runs the executable-oriented detection pipeline: the JdkFind provider suite
    /// plus the launcher-owned sources. Results keep first-discovery order; JdkFind
    /// deduplicates by canonical executable path.</summary>
    public static Task<IReadOnlyList<JavaExecutable>> DetectAsync(
        AbsolutePath? javaRoot = null,
        CancellationToken cancellationToken = default) =>
        CreateFinder(javaRoot, providers: null).LocateExecutablesAsync(cancellationToken);

    internal static Task<IReadOnlyList<JavaExecutable>> DetectAsync(
        IReadOnlyList<IJvmProvider> providers,
        AbsolutePath? javaRoot = null,
        CancellationToken cancellationToken = default) =>
        CreateFinder(javaRoot, providers).LocateExecutablesAsync(cancellationToken);

    private static JdkFinder CreateFinder(AbsolutePath? javaRoot, IReadOnlyList<IJvmProvider>? providers)
    {
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
        };
    }
}
