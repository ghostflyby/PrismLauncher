// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

using JdkFind;
using TruePath;

namespace FlintLauncher.Core.Java;

/// <summary>Exposes the JVMs under the launcher-managed java root (the JavaDir setting
/// under store semantics). Only validated homes are reported; release-less directories
/// remain the executable-directory layer's job.</summary>
internal sealed class ManagedJavaRootJvmProvider(AbsolutePath? root) : IJvmProvider
{
    public string Name => "flint-managed";

    public IEnumerable<string> GetJavaHomes()
    {
        if (root is null)
        {
            yield break;
        }

        foreach (var child in JavaProviderScan.Children(root.Value.Value))
        {
            if (JavaHomeLayout.Probe(child) is { } home)
            {
                yield return home;
            }
        }
    }
}

/// <summary>Exposes the installations named by FLINT_JAVA_PATHS. The upstream contract
/// names executable paths; each entry is mapped to the validated home above it so the
/// result can ride the provider suite. A path outside a recognizable home is ignored.</summary>
internal sealed class JavaPathsJvmProvider : IJvmProvider
{
    public string Name => "flint-java-paths";

    public IEnumerable<string> GetJavaHomes()
    {
        var value = Environment.GetEnvironmentVariable($"{FlintConfig.Default.LauncherAppBinaryEnvName}_JAVA_PATHS") ?? "";
        var paths = value.Split(OperatingSystem.IsWindows() ? ';' : ':',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var seen = new HashSet<string>(
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        foreach (var path in paths)
        {
            foreach (var home in HomesFor(path))
            {
                if (seen.Add(home))
                {
                    yield return home;
                }
            }
        }
    }

    private static IEnumerable<string> HomesFor(string path)
    {
        string? directory = Directory.Exists(path)
            ? Path.GetFullPath(path)
            : File.Exists(path)
                ? Path.GetDirectoryName(Path.GetFullPath(path))
                : null;
        if (directory is null)
        {
            yield break;
        }

        if (JavaHomeLayout.Probe(directory) is { } home)
        {
            yield return home;
        }

        if (Path.GetFileName(directory).Equals("bin", StringComparison.OrdinalIgnoreCase) &&
            Path.GetDirectoryName(directory) is { } parent &&
            JavaHomeLayout.Probe(parent) is { } parentHome)
        {
            yield return parentHome;
        }
    }
}

/// <summary>Exposes the JVMs inside the official launcher's runtime bundles. The bundle
/// nests each runtime under an arbitrary depth, so the scan walks down until a home is
/// recognized instead of assuming a fixed layout.</summary>
internal sealed class MinecraftRuntimeJvmProvider : IJvmProvider
{
    private const int MaxDepth = 8;

    public string Name => "flint-minecraft-runtime";

    public IEnumerable<string> GetJavaHomes()
    {
        foreach (var root in Roots())
        {
            foreach (var home in Scan(root))
            {
                yield return home;
            }
        }
    }

    internal static IEnumerable<string> Roots()
    {
        var home = Environment.GetEnvironmentVariable("HOME") ?? "";
        if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine(home, "Library/Application Support/minecraft/runtime");
            yield break;
        }

        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetEnvironmentVariable("APPDATA");
            if (!string.IsNullOrEmpty(appData))
            {
                yield return Path.Combine(appData, ".minecraft", "runtime");
            }

            // the Microsoft Store launcher keeps its data under the package cache
            var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (!string.IsNullOrEmpty(localAppData))
            {
                yield return Path.Combine(localAppData, "Packages",
                    "Microsoft.4297127D64EC6_8wekyb3d8bbwe", "LocalCache", "Local", "runtime");
            }

            yield break;
        }

        yield return Path.Combine(home, ".minecraft", "runtime");
    }

    private static IEnumerable<string> Scan(string root)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((root, 0));
        while (pending.Count > 0)
        {
            var (path, depth) = pending.Dequeue();
            if (JavaHomeLayout.Probe(path) is { } home)
            {
                yield return home;
                continue;
            }

            if (depth >= MaxDepth)
            {
                continue;
            }

            foreach (var child in JavaProviderScan.Children(path))
            {
                pending.Enqueue((child, depth + 1));
            }
        }
    }
}

/// <summary>The directories under the launcher-owned roots that contain a java executable.
/// They feed JdkFind's standalone candidate scan, which also identifies release-less
/// runtimes that the home providers cannot validate.</summary>
internal static class LauncherOwnedExecutableDirectories
{
    public static IEnumerable<string> ForManagedRoot(AbsolutePath? root)
    {
        if (root is null)
        {
            yield break;
        }

        foreach (var child in JavaProviderScan.Children(root.Value.Value))
        {
            foreach (var bin in JavaProviderScan.JavaBinDirectories(child))
            {
                yield return bin;
            }
        }
    }

    public static IEnumerable<string> ForMinecraftRuntime()
    {
        foreach (var root in MinecraftRuntimeJvmProvider.Roots())
        {
            foreach (var bin in JavaProviderScan.JavaBinDirectories(root))
            {
                yield return bin;
            }
        }
    }
}

internal static class JavaProviderScan
{
    private const int MaxDepth = 8;

    public static List<string> Children(string path)
    {
        try
        {
            return [.. Directory.EnumerateDirectories(path)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static IEnumerable<string> JavaBinDirectories(string root)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((root, 0));
        while (pending.Count > 0)
        {
            var (path, depth) = pending.Dequeue();
            if (HasJava(path))
            {
                yield return path;
                continue;
            }

            if (depth >= MaxDepth)
            {
                continue;
            }

            foreach (var child in Children(path))
            {
                pending.Enqueue((child, depth + 1));
            }
        }
    }

    private static bool HasJava(string directory) =>
        File.Exists(Path.Combine(directory, "java")) || File.Exists(Path.Combine(directory, "java.exe"));
}
