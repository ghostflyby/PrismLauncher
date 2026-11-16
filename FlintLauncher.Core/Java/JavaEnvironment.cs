// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

namespace FlintLauncher.Core.Java;

public static class JavaEnvironment
{
    private static readonly string[] Discarded =
    [
        "JAVA_ARGS", "CLASSPATH", "CONFIGPATH", "JAVA_HOME", "JRE_HOME",
        "_JAVA_OPTIONS", "JAVA_OPTIONS", "JAVA_TOOL_OPTIONS",
    ];

    private static readonly string[] AlwaysStripped = ["QT_PLUGIN_PATH", "QT_FONTPATH"];
    private static readonly string[] UnixStripped = ["LD_LIBRARY_PATH", "LD_PRELOAD"];

    public static SortedDictionary<string, string> Clean()
    {
        var raw = Environment.GetEnvironmentVariables();
        var environment = new SortedDictionary<string, string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (var keyObject in raw.Keys)
        {
            var key = (string)keyObject;
            if (Discarded.Contains(key) || key.StartsWith("LAUNCHER_", StringComparison.Ordinal))
            {
                continue;
            }

            var value = (string?)raw[key] ?? "";
            if (ShouldStrip(key))
            {
                value = RemoveEntries(value, (string?)raw["LAUNCHER_" + key] ?? "");
            }

            if (IsUnixLike() && key == "XMODIFIERS")
            {
                value = value.Replace("@im=ibus", "");
            }

            environment[key] = value;
        }

        if (OperatingSystem.IsLinux() && !environment.ContainsKey("LD_LIBRARY_PATH"))
        {
            environment["LD_LIBRARY_PATH"] = "";
        }

        return environment;
    }

    internal static string RemoveEntries(string target, string remove)
    {
        var separator = OperatingSystem.IsWindows() ? ';' : ':';
        var kept = target.Split(separator).ToList();
        foreach (var entry in remove.Split(separator))
        {
            kept.Remove(entry);
        }

        return string.Join(separator, kept);
    }

    private static bool ShouldStrip(string key) =>
        AlwaysStripped.Contains(key) || (IsUnixLike() && UnixStripped.Contains(key));

    private static bool IsUnixLike() =>
        OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD() || IsOpenBsd();

#pragma warning disable CA1418 // "OpenBSD" is not a platform name the analyzer knows
    private static bool IsOpenBsd() => OperatingSystem.IsOSPlatform("OpenBSD");
#pragma warning restore CA1418
}
