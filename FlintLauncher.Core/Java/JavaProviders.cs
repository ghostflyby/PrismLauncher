// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

using JdkFind;
using TruePath;

namespace FlintLauncher.Core.Java;

internal sealed class ManagedJavaRootJvmProvider(AbsolutePath? root) : IJvmProvider
{
    public string Name => "flint-managed";

    public IEnumerable<string> GetJavaHomes()
    {
        if (root is null || !Directory.Exists(root.Value.Value))
        {
            yield break;
        }

        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateDirectories(root.Value.Value);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var child in children)
        {
            if (JavaHomeLayout.Probe(child) is { } home)
            {
                yield return home;
            }
        }
    }
}

internal sealed class MinecraftRuntimeJvmProvider : IJvmProvider
{
    private const int MaxDepth = 8;

    public string Name => "flint-minecraft-runtime";

    public IEnumerable<string> GetJavaHomes()
    {
        foreach (var root in RuntimeRoots())
        {
            foreach (var home in Scan(root))
            {
                yield return home;
            }
        }
    }

    private static IEnumerable<string> RuntimeRoots()
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

            foreach (var child in Children(path))
            {
                pending.Enqueue((child, depth + 1));
            }
        }
    }

    private static List<string> Children(string path)
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
}

internal sealed class JavaPathsJvmProvider : IJvmProvider
{
    public string Name => "flint-java-paths";

    public IEnumerable<string> GetJavaHomes()
    {
        var value = Environment.GetEnvironmentVariable($"{FlintConfig.Default.LauncherAppBinaryEnvName}_JAVA_PATHS") ?? "";
        var seen = new HashSet<string>(
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        foreach (var path in value.Split(OperatingSystem.IsWindows() ? ';' : ':',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
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
        if (Directory.Exists(path))
        {
            if (JavaHomeLayout.Probe(path) is { } home)
            {
                yield return home;
            }

            yield break;
        }

        if (!File.Exists(path))
        {
            yield break;
        }

        var executable = JdkFinder.Default.FromExecutable(path);
        if (executable?.Installation?.Home is { } executableHome)
        {
            yield return executableHome.FullName;
        }
    }
}
