// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

using System.Text.Json.Serialization.Metadata;
using FlintLauncher.Core.Config;
using FlintLauncher.Core.Settings;
using TruePath;
using TruePath.SystemIo;

namespace FlintLauncher.Core.Migration;

/// <summary>The eager instance.cfg → instance.json / instance-state.json conversion.</summary>
public static class LegacyInstanceConversion
{
    /// <summary>What the pass did: how many instance directories converted, how many failed
    /// (an IO failure on one directory never stops the walk — the next pass retries it).</summary>
    public sealed record Result(int Converted, int Failed)
    {
        public static readonly Result None = new(0, 0);
    }

    /// <summary>The instance directories that still carry only the legacy cfg (an instance
    /// directory is one carrying instance.json; a cfg-only directory is migration material,
    /// invisible to discovery until this pass converts it).</summary>
    public static IReadOnlyList<AbsolutePath> Pending(IEnumerable<AbsolutePath> roots) =>
        [.. InstanceDirs(roots).Where(IsPending)];

    /// <summary>Convert every pending instance directory under the roots. Idempotent; a
    /// failing directory counts and the walk continues.</summary>
    public static Result Convert(IEnumerable<AbsolutePath> roots,
        JsonTypeInfo<InstanceConfigFile> configInfo, JsonTypeInfo<InstanceStateFile> stateInfo)
    {
        var converted = 0;
        var failed = 0;
        foreach (var dir in Pending(roots))
        {
            try
            {
                if (ConvertOne(dir, configInfo, stateInfo))
                {
                    converted++;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                          or System.Security.SecurityException or NotSupportedException
                                          or ArgumentException)
            {
                failed++;
            }
        }

        return new Result(converted, failed);
    }

    /// <summary>Convert one instance directory: instance.json materializes from the
    /// gate-folded cfg payload, the runtime time trio lands in instance-state.json, and the
    /// cfg stays untouched. Returns false when the directory is not pending.</summary>
    public static bool ConvertOne(AbsolutePath instanceDir,
        JsonTypeInfo<InstanceConfigFile> configInfo, JsonTypeInfo<InstanceStateFile> stateInfo)
    {
        if (!IsPending(instanceDir))
        {
            return false;
        }

        var ini = new IniFile();
        if (!ini.LoadFile(instanceDir / InstanceIniImporter.LegacyFileName))
        {
            return false;
        }

        // the trio's seed lands FIRST: a failure after the config half would leave the
        // directory no longer pending, and the trio has no other seed — this order keeps
        // the retry self-healing
        var statePath = instanceDir / "instance-state.json";
        if (!statePath.ExistsFile())
        {
            new TypedJsonFile<InstanceStateFile>(statePath, stateInfo).Save(new InstanceStateFile
            {
                LastLaunchTime = ReadLong(ini, "lastLaunchTime"),
                LastTimePlayed = ReadLong(ini, "lastTimePlayed"),
                TotalTimePlayed = ReadLong(ini, "totalTimePlayed"),
            });
        }

        new TypedJsonFile<InstanceConfigFile>(instanceDir / "instance.json", configInfo)
            .Save(InstanceIniImporter.Import(ini));

        return true;
    }

    private static bool IsPending(AbsolutePath dir) =>
        !(dir / "instance.json").ExistsFile() &&
        (dir / InstanceIniImporter.LegacyFileName).ExistsFile();

    private static IEnumerable<AbsolutePath> InstanceDirs(IEnumerable<AbsolutePath> roots)
    {
        foreach (var root in roots)
        {
            if (!root.ExistsDirectory())
            {
                continue;
            }

            foreach (var entry in Directory.EnumerateDirectories(root.Value))
            {
                yield return new AbsolutePath(entry);
            }
        }
    }

    private static long ReadLong(IniFile ini, string key) =>
        ini.TryGetValue(key, out var value) && value.ValueKind != IniValue.Kind.Invalid
            ? Variant.AsLong(Variant.FromIniValue(value))
            : 0;
}
