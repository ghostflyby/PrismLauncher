// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
// SPDX-FileCopyrightText: 2023 Trial97 <alexandru.tripon97@gmail.com>
// SPDX-FileCopyrightText: 2022 Sefa Eyeoglu <contact@scrumplex.net>
// SPDX-FileCopyrightText: 2022 Lenny McLennington <lenny@sneed.church>
// SPDX-FileCopyrightText: 2022 Tayou <git@tayou.org>
// SPDX-FileCopyrightText: 2023 TheKodeToad <TheKodeToad@proton.me>
// SPDX-FileCopyrightText: 2023 Rachel Powers <508861+Ryex@users.noreply.github.com>
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
// Port of launcher/tools/{BaseExternalTool,BaseProfiler,JProfiler,JVisualVM,GenericProfiler}:
// the profiler registry (Application.cpp registers "jprofiler", "jvisualvm" and "generic"),
// their settings keys/defaults, the path validation of the factories' check() and the profiler
// command lines.
//
// Deviations:
//  - The QProcess orchestration (BaseProfiler::beginProfiling/abortProfiling,
//    BaseDetachedTool::run) is not ported — the port stays data-level: it exposes the argv the
//    C++ would spawn (program + arguments) so a host can run it. GET /tools/profilers surfaces
//    the registry, each factory's check() result and that argv.
//  - JVisualVMFactory's default path uses QStandardPaths::findExecutable; the port searches the
//    PATH environment variable (with .exe appended on Windows) for "jvisualvm", falling back to
//    "visualvm", empty when not found (the C++ default is null in that case).

using System.Globalization;
using FlintLauncher.Core.Settings;
using TruePath;
using TruePath.SystemIo;

namespace FlintLauncher.Core.Tools;

/// <summary>Port of BaseExternalToolFactory: name(), the settings a factory registers, check()
/// and the command line its tool would run.</summary>
public abstract class ProfilerFactory
{
    /// <summary>The registry key (BaseProfilerFactory subclasses; Application.cpp keys:
    /// "jprofiler", "jvisualvm", "generic").</summary>
    public abstract string Key { get; }

    /// <summary>Display name of the tool (used by the UI); the C++ factories have no name()
    /// beyond the registry key.</summary>
    public abstract string Name { get; }

    /// <summary>Port of registerSettings(): registers the factory's settings keys with their
    /// defaults on the given (global) settings object.</summary>
    public abstract void RegisterSettings(SettingsObject settings);

    /// <summary>Port of check(QString* error) — validates the configured path.</summary>
    public abstract bool Check(SettingsObject settings, out string error);

    /// <summary>Port of check(const QString& path, QString* error).</summary>
    public abstract bool Check(AbsolutePath? path, out string error);

    /// <summary>
    /// Port of beginProfilingImpl(): the program and arguments the profiler would be started
    /// with (no process is spawned here). <paramref name="pid"/> is the game process id.
    /// </summary>
    public abstract (AbsolutePath Program, IReadOnlyList<string> Arguments) BuildCommand(SettingsObject settings, int pid);
}

/// <summary>Port of JProfilerFactory / JProfiler::beginProfilingImpl.</summary>
public sealed class JProfilerFactory : ProfilerFactory
{
    public const string PathKey = "JProfilerPath";
    public const string PortKey = "JProfilerPort";
    public const int DefaultPort = 42042;

    public override string Key => "jprofiler";
    public override string Name => "JProfiler";

    public override void RegisterSettings(SettingsObject settings)
    {
        settings.RegisterSetting(PathKey);
        settings.RegisterSetting(PortKey, DefaultPort);
    }

    public override bool Check(SettingsObject settings, out string error)
    {
        var raw = settings.Get(PathKey)?.ToString();
        return Check(string.IsNullOrEmpty(raw) ? null : new AbsolutePath(raw), out error);
    }

    public override bool Check(AbsolutePath? path, out string error)
    {
        error = "";
        if (path is not { } some)
        {
            error = "Empty path";
            return false;
        }

        if (!PathIo.Exists(some))
        {
            error = "Path does not exist";
            return false;
        }

        // dir.exists("bin") && (bin/jprofiler || bin/jprofiler.exe) && bin/agent.jar
        var bin = some / "bin";
        if (!bin.Exists() ||
            !((bin / "jprofiler").Exists() ||
              (bin / "jprofiler.exe").Exists()) ||
            !(bin / "agent.jar").Exists())
        {
            error = "Invalid JProfiler install";
            return false;
        }

        return true;
    }

    public override (AbsolutePath Program, IReadOnlyList<string> Arguments) BuildCommand(SettingsObject settings, int pid)
    {
        var listeningPort = settings.Get(PortKey).AsIntSetting(DefaultPort);
        var basePath = settings.Get(PathKey)?.ToString();
        // QDir(basePath).absoluteFilePath("bin/jpenable[.exe]")
        string a1 = OperatingSystem.IsWindows() ? "bin/jpenable.exe" : "bin/jpenable";
        AbsolutePath profilerProgram = string.IsNullOrEmpty(basePath)
            ? default
            : new AbsolutePath(basePath) / a1;
        var arguments = new[] { "-d", pid.ToString(), "--gui", "-p", listeningPort.ToString() };
        return (profilerProgram, arguments);
    }
}

/// <summary>Port of JVisualVMFactory / JVisualVM::beginProfilingImpl.</summary>
public sealed class JVisualVmFactory : ProfilerFactory
{
    public const string PathKey = "JVisualVMPath";

    public override string Key => "jvisualvm";
    public override string Name => "VisualVM";

    public override void RegisterSettings(SettingsObject settings)
    {
        var defaultValue = FindExecutable("jvisualvm") ?? FindExecutable("visualvm");

        settings.RegisterSetting(PathKey, defaultValue);
    }

    public override bool Check(SettingsObject settings, out string error)
    {
        var raw = settings.Get(PathKey)?.ToString();
        return Check(string.IsNullOrEmpty(raw) ? null : new AbsolutePath(raw), out error);
    }

    public override bool Check(AbsolutePath? path, out string error)
    {
        error = "";
        if (path is not { } some)
        {
            error = "Empty path";
            return false;
        }

        if (!IsExecutable(some) || !some.FileName.Contains("visualvm", StringComparison.Ordinal))
        {
            error = "Invalid path to VisualVM";
            return false;
        }

        return true;
    }

    public override (AbsolutePath Program, IReadOnlyList<string> Arguments) BuildCommand(SettingsObject settings, int pid)
    {
        var programPath = settings.Get(PathKey)?.ToString();
        return (string.IsNullOrEmpty(programPath) ? default(AbsolutePath) : new AbsolutePath(programPath),
            new[] { "--openpid", pid.ToString() });
    }

    /// <summary>The QStandardPaths::findExecutable default.</summary>
    public static AbsolutePath? FindExecutable(string executableName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
        {
            return null;
        }

        var names = OperatingSystem.IsWindows()
            ? new[] { executableName + ".exe", executableName }
            : new[] { executableName };
        foreach (var dir in pathEnv.Split(Path.PathSeparator).Select(ResolvePathEntry))
        {
            if (dir is not { } scanDir)
            {
                continue;
            }

            foreach (var name in names)
            {
                var candidate = scanDir / name;
                if (candidate.ExistsFile())
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>A PATH entry as a scannable directory: an empty entry anchors on the
    /// process CWD (the POSIX convention the string-era scan inherited); "~"-style shell
    /// shortcuts are skipped (the string-era File.Exists never found a file under a
    /// literal '~' directory either).</summary>
    private static AbsolutePath? ResolvePathEntry(string entry) =>
        entry.Length == 0 ? AbsolutePath.CurrentWorkingDirectory
        : Path.IsPathRooted(entry) ? new AbsolutePath(entry)
        : null;

    private static bool IsExecutable(AbsolutePath path)
    {
        try
        {
            if (!path.ExistsFile())
            {
                return false;
            }

            if (!OperatingSystem.IsWindows())
            {
                var mode = path.GetUnixFileMode();
                return mode.HasFlag(UnixFileMode.UserExecute) || mode.HasFlag(UnixFileMode.GroupExecute) ||
                       mode.HasFlag(UnixFileMode.OtherExecute);
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return true; // fall through to the name check, like an unreadable QFileInfo
        }
    }
}

/// <summary>Port of GenericProfilerFactory / GenericProfiler: a placeholder profiler that never
/// spawns anything (beginProfilingImpl just reports readiness).</summary>
public sealed class GenericProfilerFactory : ProfilerFactory
{
    public override string Key => "generic";
    public override string Name => "Generic";

    public override void RegisterSettings(SettingsObject settings)
    {
        // GenericProfilerFactory registers no settings upstream.
    }

    public override bool Check(SettingsObject settings, out string error)
    {
        error = "";
        return true;
    }

    public override bool Check(AbsolutePath? path, out string error)
    {
        error = "";
        return true;
    }

    public override (AbsolutePath Program, IReadOnlyList<string> Arguments) BuildCommand(SettingsObject settings, int pid) =>
        (default(AbsolutePath), []);
}

/// <summary>Port of Application's profiler registry: m_profilers and profilers().</summary>
public static class Profilers
{
    private static readonly Dictionary<string, ProfilerFactory> AllFactories = new()
    {
        ["jprofiler"] = new JProfilerFactory(),
        ["jvisualvm"] = new JVisualVmFactory(),
        ["generic"] = new GenericProfilerFactory(),
    };

    /// <summary>Port of Application::profilers().</summary>
    public static IReadOnlyDictionary<string, ProfilerFactory> All => AllFactories;

    /// <summary>Port of profilers().value(key): the instance "Profiler" setting selects one.</summary>
    public static ProfilerFactory? Value(string? key) =>
        key is not null && AllFactories.TryGetValue(key, out var factory) ? factory : null;

    /// <summary>Registers every factory's settings, like Application's tool registration loop.</summary>
    public static void RegisterSettings(SettingsObject settings)
    {
        foreach (var factory in AllFactories.Values)
        {
            factory.RegisterSettings(settings);
        }
    }
}

internal static class ProfilerStringExtensions
{
    public static int AsIntSetting(this object? value, int fallback)
    {
        try
        {
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
        {
            return fallback;
        }
    }
}
