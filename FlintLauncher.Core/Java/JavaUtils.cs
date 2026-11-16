// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022 Sefa Eyeoglu <contact@scrumplex.net>
//
// This file incorporates work covered by the following copyright and
// permission notice:
//
//     Copyright 2013-2021 MultiMC Contributors
//
//     Licensed under the Apache License, Version 2.0 (the "License");
//     you may not use this file except in compliance with the License.
//     You may obtain a copy of the License at
//
//         http://www.apache.org/licenses/LICENSE-2.0
//
//     Unless required by applicable law or agreed to in writing, software
//     distributed under the License is distributed on an "AS IS" BASIS,
//     WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//     See the License for the specific language governing permissions and
//     limitations under the License.
//
// Port of launcher/java/JavaUtils.{h,cpp}: the executable-name and architecture helpers
// plus the process-environment cleaner. JVM discovery lives in JavaDetection, backed by the
// JdkFind provider suite and the launcher-owned providers in JavaProviders.

namespace FlintLauncher.Core.Java;

public static class JavaUtils
{
    /// <summary>Port of JavaUtils::javaExecutable ("javaw.exe" on Windows, "java" elsewhere).</summary>
    public static string JavaExecutable => OperatingSystem.IsWindows() ? "javaw.exe" : "java";

    /// <summary>The os.arch spellings the launcher counts as 64-bit (shared by the detection
    /// pipeline, which derives the Mojang "32"/"64" platform string and the automatic-java
    /// 64-bit check from release/probe architecture strings of this shape).</summary>
    public static bool Is64BitArchitecture(string osArch) =>
        osArch is "x86_64" or "amd64" or "aarch64" or "arm64" or "riscv64" or "ppc64le" or "ppc64";

    // ------------------------------------------------------------- environment

    /// <summary>Port of stripVariableEntries (path-list difference, ':' or ';' separated).</summary>
    public static string StripVariableEntries(string name, string target, string remove)
    {
        var delimiter = OperatingSystem.IsWindows() ? ';' : ':';

        var targetItems = target.Split(delimiter).ToList();
        var toRemove = remove.Split(delimiter);

        foreach (var item in toRemove)
        {
            var removed = targetItems.Remove(item);
            if (!removed)
            {
                // qWarning() << "Entry" << item << "could not be stripped from variable" << name;
            }
        }

        return string.Join(delimiter, targetItems);
    }

    /// <summary>
    /// Port of CleanEnviroment: the system environment with the dangerous Java variables
    /// (JAVA_ARGS/CLASSPATH/... ) removed, LAUNCHER_* mirrors dropped and the stripped list
    /// (LD_LIBRARY_PATH/LD_PRELOAD on Linux/BSD, QT_PLUGIN_PATH/QT_FONTPATH everywhere)
    /// filtered by the corresponding LAUNCHER_* variable. Consumed by the game process
    /// environment (MinecraftInstance.createEnvironment). Variable names compare
    /// case-insensitively on Windows and ordinally on other platforms.
    /// </summary>
    public static SortedDictionary<string, string> CleanEnvironment()
    {
        var rawenv = Environment.GetEnvironmentVariables();
        var env = new SortedDictionary<string, string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        var ignored = new List<string>
        {
            "JAVA_ARGS", "CLASSPATH", "CONFIGPATH", "JAVA_HOME",
            "JRE_HOME", "_JAVA_OPTIONS", "JAVA_OPTIONS", "JAVA_TOOL_OPTIONS",
        };

        var stripped = new List<string> { "QT_PLUGIN_PATH", "QT_FONTPATH" };
        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD() || IsOpenBsd())
        {
            stripped.Add("LD_LIBRARY_PATH");
            stripped.Add("LD_PRELOAD");
        }

        foreach (var keyObject in rawenv.Keys)
        {
            var key = (string)keyObject;
            var value = (string?)rawenv[key] ?? "";
            // filter out dangerous java crap
            if (ignored.Contains(key))
            {
                continue;
            }

            // These are used to strip the original variables: if there is "LD_LIBRARY_PATH" and
            // "LAUNCHER_LD_LIBRARY_PATH", remove all values in "LAUNCHER_LD_LIBRARY_PATH" from
            // "LD_LIBRARY_PATH".
            if (key.StartsWith("LAUNCHER_", StringComparison.Ordinal))
            {
                continue;
            }

            if (stripped.Contains(key))
            {
                value = StripVariableEntries(key, value, (string?)rawenv["LAUNCHER_" + key] ?? "");
            }

            if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD() || IsOpenBsd())
            {
                // Strip IBus (Linux IME framework; for some reason it breaks MC)
                if (key == "XMODIFIERS" && value.Contains("@im=ibus"))
                {
                    value = value.Replace("@im=ibus", "");
                }
            }

            env[key] = value;
        }

        if (OperatingSystem.IsLinux())
        {
            // HACK: Workaround for QTBUG-42500
            if (!env.ContainsKey("LD_LIBRARY_PATH"))
            {
                env["LD_LIBRARY_PATH"] = "";
            }
        }

        return env;
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>OperatingSystem has no IsOpenBSD() helper (see RuntimeContext for the same shim).</summary>
#pragma warning disable CA1418 // "OpenBSD" is not a platform name the analyzer knows
    private static bool IsOpenBsd() => OperatingSystem.IsOSPlatform("OpenBSD");
#pragma warning restore CA1418

}
