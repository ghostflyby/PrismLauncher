// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/tools/{BaseExternalTool,BaseProfiler,JProfiler,JVisualVM,GenericProfiler}:
// the profiler registry (Application.cpp registers "jprofiler", "jvisualvm" and "generic"),
// their settings keys/defaults, the path validation of the factories' check() and the profiler
// command lines.
//
// Deviations:
//  - The QProcess orchestration (BaseProfiler::beginProfiling/abortProfiling,
//    BaseDetachedTool::run) is not ported — the port stays data-level: it exposes the argv the
//    C++ would spawn (program + arguments) so a host can run it.
//  - JVisualVMFactory's default path uses QStandardPaths::findExecutable; the port searches the
//    PATH environment variable (with .exe appended on Windows) for "jvisualvm", falling back to
//    "visualvm", empty when not found (the C++ default is null in that case).

using System.Globalization;
using FlintLauncher.Core.Settings;

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
    public abstract bool Check(string path, out string error);

    /// <summary>
    /// Port of beginProfilingImpl(): the program and arguments the profiler would be started
    /// with (no process is spawned here). <paramref name="pid"/> is the game process id.
    /// </summary>
    public abstract (string Program, IReadOnlyList<string> Arguments) BuildCommand(SettingsObject settings, int pid);
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

    public override bool Check(SettingsObject settings, out string error) =>
        Check(settings.Get(PathKey)?.ToString() ?? "", out error);

    public override bool Check(string path, out string error)
    {
        error = "";
        if (path.IsEmpty())
        {
            error = "Empty path";
            return false;
        }
        if (!Directory.Exists(path))
        {
            error = "Path does not exist";
            return false;
        }
        // dir.exists("bin") && (bin/jprofiler || bin/jprofiler.exe) && bin/agent.jar
        var bin = Path.Combine(path, "bin");
        if (!Directory.Exists(bin) ||
            !(FsExists(Path.Combine(bin, "jprofiler")) ||
              FsExists(Path.Combine(bin, "jprofiler.exe"))) ||
            !FsExists(Path.Combine(bin, "agent.jar")))
        {
            error = "Invalid JProfiler install";
            return false;
        }
        return true;
    }

    /// <summary>QDir::exists accepts files and directories.</summary>
    private static bool FsExists(string fsPath) => File.Exists(fsPath) || Directory.Exists(fsPath);

    public override (string Program, IReadOnlyList<string> Arguments) BuildCommand(SettingsObject settings, int pid)
    {
        var listeningPort = settings.Get(PortKey).AsIntSetting(DefaultPort);
        var basePath = settings.Get(PathKey)?.ToString() ?? "";
        // QDir(basePath).absoluteFilePath("bin/jpenable[.exe]")
        var profilerProgram = Path.Combine(basePath, OperatingSystem.IsWindows() ? "bin/jpenable.exe" : "bin/jpenable");
        var arguments = new[] { "-d", pid.ToStringInvariant(), "--gui", "-p", listeningPort.ToStringInvariant() };
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
        var defaultValue = FindExecutable("jvisualvm");
        if (defaultValue.Length == 0)
        {
            defaultValue = FindExecutable("visualvm");
        }
        settings.RegisterSetting(PathKey, defaultValue);
    }

    public override bool Check(SettingsObject settings, out string error) =>
        Check(settings.Get(PathKey)?.ToString() ?? "", out error);

    public override bool Check(string path, out string error)
    {
        error = "";
        if (path.IsEmpty())
        {
            error = "Empty path";
            return false;
        }
        // QFileInfo::isExecutable() && fileName().contains("visualvm")
        if (!IsExecutable(path) || !Path.GetFileName(path).Contains("visualvm", StringComparison.Ordinal))
        {
            error = "Invalid path to VisualVM";
            return false;
        }
        return true;
    }

    public override (string Program, IReadOnlyList<string> Arguments) BuildCommand(SettingsObject settings, int pid)
    {
        var programPath = settings.Get(PathKey)?.ToString() ?? "";
        return (programPath, new[] { "--openpid", pid.ToStringInvariant() });
    }

    /// <summary>The QStandardPaths::findExecutable default.</summary>
    public static string FindExecutable(string executableName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
        {
            return "";
        }
        var names = OperatingSystem.IsWindows()
            ? new[] { executableName + ".exe", executableName }
            : new[] { executableName };
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            if (dir.Length == 0)
            {
                continue;
            }
            foreach (var name in names)
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        return "";
    }

    private static bool IsExecutable(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }
            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(path);
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

    public override bool Check(string path, out string error)
    {
        error = "";
        return true;
    }

    public override (string Program, IReadOnlyList<string> Arguments) BuildCommand(SettingsObject settings, int pid) =>
        ("", []);
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
    public static bool IsEmpty(this string s) => s.Length == 0;

    public static string ToStringInvariant(this int value) => value.ToString(CultureInfo.InvariantCulture);

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
